using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Sse;

namespace Nodus.Tests.Integration;

public class NodeSyncTests(TestApp app) : IClassFixture<TestApp>
{
    private static object OneTime(Guid id, string title = "Купить хлеб") => new
    {
        id, type = "one_time", title, timezone = "Europe/Moscow", date = "2026-11-02", time = "18:30"
    };

    private static object Daily(Guid id, string time = "09:00", string title = "Зарядка") => new
    {
        id, type = "recurring", title, timezone = "Europe/Moscow",
        recurrence = new { frequency = "daily", start_date = "2026-10-01", times = new[] { time } }
    };

    private static string Key() => Guid.NewGuid().ToString();

    private async Task<JsonElement> SyncAsync(TestUser user, long cursor) =>
        await (await user.Client.GetAsync($"/v1/sync?cursor={cursor}")).ReadJsonAsync();

    [Fact]
    public async Task Mutations_require_an_idempotency_key()
    {
        var user = await app.CreateUserAsync();

        var response = await user.Client.PostJsonAsync("/v1/nodes", OneTime(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_required", (await response.ReadJsonAsync()).Code());
    }

    [Fact]
    public async Task Retrying_with_the_same_key_replays_without_duplicates_and_different_payload_is_rejected()
    {
        var user = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        var key = Key();

        var first = await user.Client.PostJsonAsync("/v1/nodes", OneTime(id), key);
        var retry = await user.Client.PostJsonAsync("/v1/nodes", OneTime(id), key);
        var reused = await user.Client.PostJsonAsync("/v1/nodes", OneTime(id, "Другое"), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(1, await app.WithDbAsync(db => db.Nodes.CountAsync(n => n.Id == id)));
        Assert.Single((await SyncAsync(user, 0)).GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_create_one_node()
    {
        var user = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        var key = Key();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => user.Client.PostJsonAsync("/v1/nodes", OneTime(id), key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(1, await app.WithDbAsync(db => db.Nodes.CountAsync(n => n.Id == id)));
    }

    [Fact]
    public async Task Stale_version_gets_409_with_canonical_state()
    {
        var user = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        await user.Client.PostJsonAsync("/v1/nodes", OneTime(id), Key());
        var edit = new { expected_version = 1, title = "Хлеб и молоко", timezone = "Europe/Moscow", date = "2026-11-02" };

        var ok = await user.Client.SendJsonAsync(HttpMethod.Patch, $"/v1/nodes/{id}", edit, Key());
        var stale = await user.Client.SendJsonAsync(HttpMethod.Patch, $"/v1/nodes/{id}", edit with { }, Key());
        var problem = await stale.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version_conflict", problem.Code());
        Assert.Equal(2, problem.GetProperty("current").GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Batch_is_atomic_and_keeps_client_ids()
    {
        var user = await app.CreateUserAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var invalid = await user.Client.PostJsonAsync("/v1/nodes/batch",
            new { nodes = new[] { OneTime(a), new { id = b, type = "one_time", title = "", timezone = "Europe/Moscow" } } }, Key());
        var valid = await user.Client.PostJsonAsync("/v1/nodes/batch", new { nodes = new[] { OneTime(a), Daily(b) } }, Key());

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await invalid.ReadJsonAsync()).GetProperty("errors").TryGetProperty("nodes[1].title", out _));
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        Assert.Equal([a, b], (await valid.ReadJsonAsync()).GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Untie_is_per_occurrence_and_survives_template_edits()
    {
        app.Time.Set(DateTimeOffset.Parse("2026-10-17T10:00:00+03:00"));
        var user = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        await user.Client.PostJsonAsync("/v1/nodes", Daily(id), Key());

        var untie = await user.Client.SendJsonAsync(HttpMethod.Put, $"/v1/nodes/{id}/occurrences/g0-20261016-0/untie", new { }, Key());
        var edit = await user.Client.SendJsonAsync(HttpMethod.Patch, $"/v1/nodes/{id}", new
        {
            expected_version = 1, title = "Утренняя зарядка", timezone = "Europe/Moscow",
            recurrence = new { frequency = "daily", start_date = "2026-10-01", times = new[] { "07:30" } }
        }, Key());
        var history = await (await user.Client.GetAsync($"/v1/nodes/{id}/occurrences?from=2026-10-15&to=2026-10-18")).ReadJsonAsync();
        var occurrences = history.GetProperty("occurrences").EnumerateArray()
            .Select(o => (Date: o.GetProperty("date").GetString(), Title: o.GetProperty("title").GetString(),
                Time: o.GetProperty("time").GetString(), State: o.GetProperty("state").GetString()))
            .ToList();

        Assert.Equal(HttpStatusCode.OK, untie.StatusCode);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal([
            ("2026-10-15", "Зарядка", "09:00", "tied"),
            ("2026-10-16", "Зарядка", "09:00", "untied"),
            ("2026-10-17", "Зарядка", "09:00", "tied"),
            ("2026-10-18", "Утренняя зарядка", "07:30", "tied")
        ], occurrences);
    }

    [Fact]
    public async Task Unknown_occurrence_keys_are_rejected()
    {
        var user = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        await user.Client.PostJsonAsync("/v1/nodes", Daily(id), Key());

        var response = await user.Client.SendJsonAsync(HttpMethod.Put, $"/v1/nodes/{id}/occurrences/g0-20200101-0/untie", null, Key());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("occurrence_not_found", (await response.ReadJsonAsync()).Code());
    }

    [Fact]
    public async Task Delta_sync_pages_in_order_and_returns_tombstones()
    {
        var user = await app.CreateUserAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await user.Client.PostJsonAsync("/v1/nodes", OneTime(a), Key());
        await user.Client.PostJsonAsync("/v1/nodes", OneTime(b), Key());
        await user.Client.SendJsonAsync(HttpMethod.Put, $"/v1/nodes/{a}/occurrences/once/untie", null, Key());
        await user.Client.SendJsonAsync(HttpMethod.Delete, $"/v1/nodes/{b}?expected_version=1", null, Key());

        var first = await (await user.Client.GetAsync("/v1/sync?cursor=0&limit=2")).ReadJsonAsync();
        var rest = await SyncAsync(user, first.GetProperty("next_cursor").GetInt64());
        var changes = first.GetProperty("changes").EnumerateArray().Concat(rest.GetProperty("changes").EnumerateArray())
            .Select(c => (Type: c.GetProperty("entity_type").GetString(), Op: c.GetProperty("operation").GetString(),
                Revision: c.GetProperty("revision").GetInt64()))
            .ToList();

        Assert.True(first.GetProperty("has_more").GetBoolean());
        Assert.False(rest.GetProperty("has_more").GetBoolean());
        Assert.Equal([("node", "upsert"), ("node", "delete"), ("occurrence_state", "upsert"), ("node", "delete")],
            changes.Select(c => (c.Type, c.Op)));
        Assert.True(changes.Zip(changes.Skip(1)).All(p => p.First.Revision < p.Second.Revision));
        Assert.Equal(4, rest.GetProperty("next_cursor").GetInt64());
        Assert.Empty((await SyncAsync(user, 4)).GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task Users_cannot_see_or_change_each_others_nodes()
    {
        var owner = await app.CreateUserAsync();
        var stranger = await app.CreateUserAsync();
        var id = Guid.NewGuid();
        await owner.Client.PostJsonAsync("/v1/nodes", OneTime(id), Key());

        var read = await stranger.Client.GetAsync($"/v1/nodes/{id}");
        var patch = await stranger.Client.SendJsonAsync(HttpMethod.Patch, $"/v1/nodes/{id}",
            new { expected_version = 1, title = "x", timezone = "Europe/Moscow" }, Key());
        var untie = await stranger.Client.SendJsonAsync(HttpMethod.Put, $"/v1/nodes/{id}/occurrences/once/untie", null, Key());
        var delete = await stranger.Client.SendJsonAsync(HttpMethod.Delete, $"/v1/nodes/{id}", null, Key());
        var takeOverId = await stranger.Client.PostJsonAsync("/v1/nodes", OneTime(id), Key());
        var feed = await (await stranger.Client.GetAsync("/v1/feed")).ReadJsonAsync();

        Assert.All([read, patch, untie, delete], r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(HttpStatusCode.Conflict, takeOverId.StatusCode);
        Assert.Empty(feed.GetProperty("items").EnumerateArray());
        Assert.Empty((await SyncAsync(stranger, 0)).GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task Expired_cursor_requires_snapshot()
    {
        var user = await app.CreateUserAsync();
        await user.Client.PostJsonAsync("/v1/nodes", OneTime(Guid.NewGuid()), Key());
        await app.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SyncPrunedRevision, 1)));

        var expired = await user.Client.GetAsync("/v1/sync?cursor=0");
        var snapshot = await (await user.Client.GetAsync("/v1/sync/snapshot")).ReadJsonAsync();

        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal("cursor_expired", (await expired.ReadJsonAsync()).Code());
        Assert.Equal(1, snapshot.GetProperty("cursor").GetInt64());
        Assert.Single(snapshot.GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public async Task Feed_shows_today_and_next_for_series()
    {
        app.Time.Set(DateTimeOffset.Parse("2026-10-17T10:00:00+03:00"));
        var user = await app.CreateUserAsync();
        await user.Client.PostJsonAsync("/v1/nodes", Daily(Guid.NewGuid(), "20:00"), Key());

        var feed = await (await user.Client.GetAsync("/v1/feed?from=2026-10-01&to=2026-12-31")).ReadJsonAsync();

        Assert.Equal(["2026-10-17", "2026-10-18"],
            feed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("occurrence").GetProperty("date").GetString()));
    }

    [Fact]
    public async Task Committed_changes_reach_the_outbox_and_realtime_subscribers()
    {
        var user = await app.CreateUserAsync();
        using var subscription = app.Services.GetRequiredService<IRealtimeHub>().Subscribe(user.Id);

        await user.Client.PostJsonAsync("/v1/nodes", OneTime(Guid.NewGuid()), Key());
        await app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var signal = await subscription.Signals.ReadAsync(timeout.Token);
        Assert.Equal(1, signal.Revision);
    }
}
