using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api.Infrastructure.Apns;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Notifications;

namespace Nodus.Tests.Integration;

public class RealtimeAndPushTests(TestApp app) : IClassFixture<TestApp>
{
    private static string Token() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    private static object Node() => new
    {
        id = Guid.NewGuid(), type = "one_time", title = "Позвонить", timezone = "Europe/Moscow"
    };

    [Fact]
    public async Task Device_registration_is_an_upsert_and_a_token_belongs_to_one_installation()
    {
        var first = await app.CreateUserAsync();
        var second = await app.CreateUserAsync();
        var token = Token();

        var created = await first.Client.PostJsonAsync("/v1/devices",
            new { installation_id = "phone-1", apns_token = token.ToUpperInvariant(), apns_environment = "sandbox" });
        var updated = await first.Client.PostJsonAsync("/v1/devices", new { installation_id = "phone-1", app_version = "1.2" });
        var moved = await second.Client.PostJsonAsync("/v1/devices", new { installation_id = "phone-1", apns_token = token });
        var invalid = await first.Client.PostJsonAsync("/v1/devices", new { installation_id = "phone-1", apns_token = "not-hex" });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal((await created.ReadJsonAsync()).GetProperty("id").GetGuid(), (await updated.ReadJsonAsync()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var owners = await app.WithDbAsync(db => db.Devices.Where(d => d.ApnsToken == token).Select(d => d.UserId).ToListAsync());
        Assert.Equal([second.Id], owners);
    }

    [Fact]
    public async Task Push_is_sent_to_all_installations_after_commit_and_dead_tokens_are_cleared()
    {
        var user = await app.CreateUserAsync();
        var (alive, dead) = (Token(), Token());
        await user.Client.PostJsonAsync("/v1/devices", new { installation_id = "a", apns_token = alive, apns_environment = "production" });
        await user.Client.PostJsonAsync("/v1/devices", new { installation_id = "b", apns_token = dead, apns_environment = "production" });
        app.Apns.Outcomes[dead] = ApnsOutcome.InvalidToken;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<PushNotifier>().Request(new PushRequest(user.Id, "evt-1",
                PushKinds.AiDraftsReady, "Черновики готовы", "Откройте Nodus", new Dictionary<string, string> { ["job_id"] = "1" }));
            await scope.ServiceProvider.GetRequiredService<NodusDbContext>().SaveChangesAsync();
        }

        await app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);

        var sentTo = app.Apns.Sent.Where(s => s.Alert.Data["event_id"] == "evt-1").Select(s => s.Token).Order().ToList();
        var tokens = await app.WithDbAsync(db => db.Devices.Where(d => d.UserId == user.Id)
            .OrderBy(d => d.InstallationId).Select(d => d.ApnsToken).ToListAsync());
        Assert.Equal(new[] { alive, dead }.Order(), sentTo);
        Assert.Equal([alive, null], tokens);
    }

    [Fact]
    public async Task Sse_signals_immediately_when_behind_and_then_on_new_changes()
    {
        var user = await app.CreateUserAsync();
        await user.Client.PostJsonAsync("/v1/nodes", Node(), Guid.NewGuid().ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/events");
        request.Headers.Add("Last-Event-ID", "0");
        using var response = await user.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(("1", "sync.required"), await NextEventAsync(reader));

        await user.Client.PostJsonAsync("/v1/nodes", Node(), Guid.NewGuid().ToString());
        await app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(("2", "sync.required"), await NextEventAsync(reader));
    }

    [Fact]
    public async Task Sse_stays_quiet_when_client_is_up_to_date()
    {
        var user = await app.CreateUserAsync();
        await user.Client.PostJsonAsync("/v1/nodes", Node(), Guid.NewGuid().ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/events?last_event_id=1");
        using var response = await user.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await user.Client.PostJsonAsync("/v1/nodes", Node(), Guid.NewGuid().ToString());
        await app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);

        Assert.Equal(("2", "sync.required"), await NextEventAsync(reader));
    }

    private static async Task<(string Id, string Event)> NextEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? id = null, name = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("id: ")) id = line[4..];
            else if (line.StartsWith("event: ")) name = line[7..];
            else if (line.Length == 0 && name is not null) return (id!, name);
        }

        throw new InvalidOperationException("Stream ended.");
    }
}
