using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api;
using Nodus.Api.Common;
using Nodus.Api.Devices;
using Nodus.Api.Infrastructure.Apns;
using Nodus.Api.Infrastructure.Llm;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Infrastructure.Resend;
using Npgsql;

namespace Nodus.Tests.Integration;

/// <summary>
/// The real application on a TestServer against a throwaway PostgreSQL database.
/// Server: <c>NODUS_TEST_POSTGRES</c> or the local test container
/// (<c>docker run -d -p 55432:5432 -e POSTGRES_USER=nodus -e POSTGRES_PASSWORD=nodus ankane/pgvector</c>).
/// </summary>
public sealed class TestApp : IAsyncLifetime
{
    private static readonly string ServerConnection =
        Environment.GetEnvironmentVariable("NODUS_TEST_POSTGRES")
        ?? "Host=localhost;Port=55432;Username=nodus;Password=nodus;Database=postgres";

    private readonly string _database = $"nodus_test_{Guid.NewGuid():N}";
    private WebApplication? _app;

    public ManualTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public CapturingEmailSender Emails { get; } = new();
    public FakeApnsClient Apns { get; } = new();
    public FakeLlmClient Llm { get; } = new();
    public IServiceProvider Services => _app!.Services;
    public Dictionary<string, string?> Settings { get; } = [];

    public async Task InitializeAsync()
    {
        await using (var connection = new NpgsqlConnection(ServerConnection))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {_database}", connection).ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ServerConnection) { Database = _database }.ToString();
        _app = NodusApplication.Build([], builder =>
        {
            builder.Environment.EnvironmentName = "Testing";
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Jwt:SigningKey"] = "test-signing-key-that-is-long-enough-0123456789",
                ["EmailOtp:HashPepper"] = "test-pepper-0123456789",
                ["Hangfire:Enabled"] = "false",
                ["Outbox:RunInBackground"] = "false",
                ["RateLimits:AuthPerMinute"] = "1000",
                ["RateLimits:OtpPerMinute"] = "1000",
                ["RateLimits:AiPerMinute"] = "1000"
            }.Concat(Settings));
            builder.Services.AddSingleton<TimeProvider>(Time);
            builder.Services.AddSingleton<IEmailSender>(Emails);
        }, services =>
        {
            services.AddSingleton<IApnsClient>(Apns);
            services.AddSingleton<ILlmClient>(Llm);
        });

        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NodusDbContext>().Database.MigrateAsync();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(ServerConnection);
        await connection.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", connection).ExecuteNonQueryAsync();
    }

    public HttpClient CreateClient(string? accessToken = null)
    {
        var client = _app!.GetTestServer().CreateClient();
        if (accessToken is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Registers an anonymous user and returns a client authenticated as that user.</summary>
    public async Task<TestUser> CreateUserAsync()
    {
        var response = await CreateClient().PostAsync("/v1/auth/anonymous", null);
        response.EnsureSuccessStatusCode();
        var body = await response.ReadJsonAsync();
        var accessToken = body.GetProperty("access_token").GetString()!;
        return new TestUser(body.GetProperty("user_id").GetGuid(), accessToken, body.GetProperty("refresh_token").GetString()!,
            CreateClient(accessToken));
    }

    public async Task<T> WithDbAsync<T>(Func<NodusDbContext, Task<T>> action)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<NodusDbContext>());
    }
}

public sealed record TestUser(Guid Id, string AccessToken, string RefreshToken, HttpClient Client);

public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset value) => _now = value.ToUniversalTime();
}

public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<OutgoingEmail> _sent = new();

    public IReadOnlyCollection<OutgoingEmail> Sent => _sent;

    public Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        _sent.Enqueue(email);
        return Task.CompletedTask;
    }

    public string LastCodeFor(string to) =>
        CodePattern().Match(_sent.Last(e => e.To == to).HtmlBody).Groups[1].Value;

    [GeneratedRegex("<strong>(\\d{6})</strong>")]
    private static partial Regex CodePattern();
}

public sealed class FakeApnsClient : IApnsClient
{
    public ConcurrentQueue<(string Token, ApnsAlert Alert)> Sent { get; } = new();
    public ConcurrentDictionary<string, ApnsOutcome> Outcomes { get; } = new();

    public Task<ApnsResult> SendAsync(string deviceToken, ApnsEnvironment environment, ApnsAlert alert, CancellationToken ct)
    {
        Sent.Enqueue((deviceToken, alert));
        return Task.FromResult(new ApnsResult(Outcomes.GetValueOrDefault(deviceToken, ApnsOutcome.Sent)));
    }
}

public sealed class FakeLlmClient : ILlmClient
{
    /// <summary>Default: no nodes recognised.</summary>
    public Func<LlmRequest, string> Respond { get; set; } = _ => """{"nodes":[]}""";

    public ConcurrentQueue<LlmRequest> Requests { get; } = new();

    public Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new LlmResult(Respond(request), "fake", "fake-model", 10, 20, TimeSpan.FromMilliseconds(5), null));
    }
}

public static class HttpTestExtensions
{
    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object? body,
        string? idempotencyKey = null) => SendJsonAsync(client, HttpMethod.Post, url, body, idempotencyKey);

    public static async Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string url,
        object? body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: NodusJson.Options);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    public static string Code(this JsonElement problem) => problem.GetProperty("code").GetString()!;
}
