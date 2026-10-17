using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Tests.Integration;

public class OutboxFailureTests : IAsyncLifetime
{
    private readonly FlakyHandler _handler = new();
    private readonly TestApp _app;

    public OutboxFailureTests()
    {
        _app = new TestApp
        {
            ExtraServices = s => s.AddSingleton<IOutboxHandler>(_handler),
        };
        _app.Settings["Outbox:MaxAttempts"] = "3";
    }

    public Task InitializeAsync() => _app.InitializeAsync();
    public Task DisposeAsync() => _app.DisposeAsync();

    private async Task<long> AddEventAsync() => await _app.WithDbAsync(async db =>
    {
        var e = OutboxEvent.Create(FlakyHandler.Type, "{}", _app.Time.GetUtcNow());
        db.OutboxEvents.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    });

    private Task<OutboxEvent> LoadAsync(long id) => _app.WithDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.Id == id));

    private async Task DispatchAsync()
    {
        await _app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);
        await _app.WithDbAsync(db => db.OutboxEvents.ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, (DateTimeOffset?)null)));
    }

    [Fact]
    public async Task Failed_delivery_is_retried_with_backoff_and_succeeds_once()
    {
        _handler.FailuresLeft = 1;
        var id = await AddEventAsync();

        await _app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);
        var afterFailure = await LoadAsync(id);
        await _app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None); // backoff not over
        await DispatchAsync();
        await DispatchAsync();

        Assert.Equal((1, "InvalidOperationException"), (afterFailure.Attempts, afterFailure.LastError));
        Assert.NotNull(afterFailure.NextAttemptAt);
        Assert.Equal(1, _handler.Successes);
        Assert.NotNull((await LoadAsync(id)).ProcessedAt);
    }

    [Fact]
    public async Task Delivery_is_abandoned_after_max_attempts()
    {
        _handler.FailuresLeft = int.MaxValue;
        var id = await AddEventAsync();

        for (var i = 0; i < 5; i++) await DispatchAsync();

        var e = await LoadAsync(id);
        Assert.Equal(3, e.Attempts);
        Assert.NotNull(e.FailedAt);
        Assert.Equal(0, _handler.Successes);
    }

    private sealed class FlakyHandler : IOutboxHandler
    {
        public const string Type = "test.flaky";
        public int FailuresLeft;
        public int Successes;

        public string EventType => Type;

        public Task HandleAsync(OutboxEvent outboxEvent, CancellationToken ct)
        {
            if (FailuresLeft-- > 0) throw new InvalidOperationException("boom");
            Successes++;
            return Task.CompletedTask;
        }
    }
}

public class SecurityTests(TestApp app) : IClassFixture<TestApp>
{
    private string Token(string key, string audience = "nodus-ios", string algorithm = SecurityAlgorithms.HmacSha256) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "nodus", Audience = audience, Expires = app.Time.GetUtcNow().UtcDateTime.AddMinutes(5),
            IssuedAt = app.Time.GetUtcNow().UtcDateTime, NotBefore = app.Time.GetUtcNow().UtcDateTime,
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())]),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm)
        });

    [Fact]
    public async Task Tokens_with_wrong_key_audience_or_no_signature_are_rejected()
    {
        const string realKey = "test-signing-key-that-is-long-enough-0123456789";
        var unsigned = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "nodus", Audience = "nodus-ios", Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())])
        });

        foreach (var token in new[] { Token("another-signing-key-that-is-long-enough-012345"), Token(realKey, "other-app"), unsigned })
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient(token).GetAsync("/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Foreign_and_missing_nodes_are_indistinguishable()
    {
        var owner = await app.CreateUserAsync();
        var stranger = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        await owner.Client.PostJsonAsync("/v1/nodes", new { id, type = "one_time", title = "Секрет", timezone = "Europe/Moscow" },
            Guid.NewGuid().ToString());

        var foreign = await stranger.Client.GetAsync($"/v1/nodes/{id}");
        var missing = await stranger.Client.GetAsync($"/v1/nodes/{Guid.NewGuid()}");

        Assert.Equal((HttpStatusCode.NotFound, await missing.Content.ReadAsStringAsync()),
            (foreign.StatusCode, await foreign.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_per_user()
    {
        var first = await app.CreateUserAsync();
        var second = await app.CreateUserAsync();
        var key = Guid.NewGuid().ToString();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        await first.Client.PostJsonAsync("/v1/nodes", new { id = a, type = "one_time", title = "A", timezone = "Europe/Moscow" }, key);
        var other = await second.Client.PostJsonAsync("/v1/nodes", new { id = b, type = "one_time", title = "B", timezone = "Europe/Moscow" }, key);

        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.False(other.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(b, (await other.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Oversized_and_malformed_bodies_are_rejected_cleanly()
    {
        var user = await app.CreateUserAsync();

        var malformed = await user.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/nodes")
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } }
        });
        var tooLong = await user.Client.PostJsonAsync("/v1/nodes", new
        {
            id = Guid.NewGuid(), type = "one_time", title = new string('x', Limits.TitleLength + 1), timezone = "Europe/Moscow"
        }, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_a_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Request-Id", "client-req-12345");

        var echoed = await app.CreateClient().SendAsync(request);
        var generated = await app.CreateClient().GetAsync("/health/live");

        Assert.Equal("client-req-12345", echoed.Headers.GetValues("X-Request-Id").Single());
        Assert.False(string.IsNullOrEmpty(generated.Headers.GetValues("X-Request-Id").Single()));
    }
}
