using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Plans;

namespace Nodus.Tests.Integration;

public class PlanTests(TestApp app) : IClassFixture<TestApp>
{
    private const string OneDraft = """
        {"nodes":[{"type":"one_time","title":"Купить хлеб","description":null,"date":null,"time":null,"recurrence":null,"track_years":null,"ambiguities":[]}]}
        """;

    private static string Key() => Guid.NewGuid().ToString();

    private static object OneTime(Guid id, string title) => new { id, type = "one_time", title, timezone = "Europe/Moscow" };

    private Task<HttpResponseMessage> CreateDraftJobAsync(TestUser user, string? key = null) =>
        user.Client.PostJsonAsync("/v1/ai/node-drafts",
            new { text = "Завтра купить хлеб", timezone = "Europe/Moscow", reference_time = app.Time.GetUtcNow() }, key ?? Key());

    private Task<HttpResponseMessage> ConsentAsync(TestUser user, bool granted) =>
        user.Client.SendJsonAsync(HttpMethod.Put, "/v1/me/ai-consent", new { granted }, Key());

    private Task ProcessOutboxAsync() =>
        app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);

    private async Task<JsonElement> MeAsync(TestUser user) => await (await user.Client.GetAsync("/v1/me")).ReadJsonAsync();

    private static string[] FeaturesOf(JsonElement profile) =>
        [.. profile.GetProperty("features").EnumerateArray().Select(f => f.GetString()!)];

    private async Task<int> ContinuationRequestsAsync(Guid nodeId)
    {
        var payloads = await app.WithDbAsync(db => db.OutboxEvents
            .Where(e => e.Type == OutboxEventTypes.AiContinuationRequested).Select(e => e.Payload).ToListAsync());
        return payloads.Count(p => p.Contains(nodeId.ToString()));
    }

    [Fact]
    public async Task New_user_has_no_features_and_ai_requests_are_rejected_without_storing_the_key()
    {
        var user = await app.CreateUserAsync();
        var key = Key();

        var me = await MeAsync(user);
        var denied = await CreateDraftJobAsync(user, key);
        var problem = await denied.ReadJsonAsync();
        await app.SetPlanAsync(user.Id, UserPlan.Plus);
        await ConsentAsync(user, true);
        var retried = await CreateDraftJobAsync(user, key);

        Assert.Empty(FeaturesOf(me));
        Assert.Equal(JsonValueKind.Null, me.GetProperty("ai_consent_at").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(("feature_not_available", "ai_drafts"), (problem.Code(), problem.GetProperty("feature").GetString()));
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        Assert.False(retried.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task Plan_without_consent_is_rejected_with_a_separate_code()
    {
        var user = await app.CreateUserAsync();
        await app.SetPlanAsync(user.Id, UserPlan.Plus);

        var me = await MeAsync(user);
        var denied = await (await CreateDraftJobAsync(user)).ReadJsonAsync();
        await ConsentAsync(user, true);
        var allowed = await CreateDraftJobAsync(user);

        Assert.Equal(["ai_continuations", "ai_drafts", "ai_import"], FeaturesOf(me));
        Assert.Equal(("ai_consent_required", "ai_drafts"), (denied.Code(), denied.GetProperty("feature").GetString()));
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
    }

    [Fact]
    public async Task Consent_is_kept_on_repeat_withdrawn_and_synced()
    {
        var user = await app.CreateUserAsync();

        var granted = await (await ConsentAsync(user, true)).ReadJsonAsync();
        app.Time.Advance(TimeSpan.FromMinutes(5));
        var repeated = await (await ConsentAsync(user, true)).ReadJsonAsync();
        var withdrawn = await (await ConsentAsync(user, false)).ReadJsonAsync();
        var invalid = await user.Client.SendJsonAsync(HttpMethod.Put, "/v1/me/ai-consent", new { }, Key());
        var sync = await (await user.Client.GetAsync("/v1/sync?cursor=0&limit=1000")).ReadJsonAsync();

        Assert.Equal(granted.GetProperty("ai_consent_at").GetString(), repeated.GetProperty("ai_consent_at").GetString());
        Assert.Equal(granted.GetProperty("version").GetInt64(), repeated.GetProperty("version").GetInt64());
        Assert.Equal(JsonValueKind.Null, withdrawn.GetProperty("ai_consent_at").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var last = sync.GetProperty("changes").EnumerateArray().Last(c => c.GetProperty("entity_type").GetString() == "user");
        Assert.Equal(withdrawn.GetProperty("version").GetInt64(), last.GetProperty("data").GetProperty("version").GetInt64());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("data").GetProperty("ai_consent_at").ValueKind);
    }

    [Fact]
    public async Task Granting_and_revoking_a_plan_reaches_devices_through_sync()
    {
        var user = await app.CreateUserAsync();
        var cursor = (await (await user.Client.GetAsync("/v1/sync/snapshot")).ReadJsonAsync()).GetProperty("cursor").GetInt64();

        var granted = await app.SetPlanAsync(user.Id, UserPlan.Plus);
        var repeated = await app.SetPlanAsync(user.Id, UserPlan.Plus);
        var afterGrant = await (await user.Client.GetAsync($"/v1/sync?cursor={cursor}")).ReadJsonAsync();
        await app.SetPlanAsync(user.Id, UserPlan.Standard);
        var snapshot = await (await user.Client.GetAsync("/v1/sync/snapshot")).ReadJsonAsync();

        Assert.Equal((PlanChangeOutcome.Changed, PlanChangeOutcome.Unchanged), (granted, repeated));
        var change = Assert.Single(afterGrant.GetProperty("changes").EnumerateArray());
        Assert.Equal("user", change.GetProperty("entity_type").GetString());
        Assert.Equal(["ai_continuations", "ai_drafts", "ai_import"], FeaturesOf(change.GetProperty("data")));
        Assert.Empty(FeaturesOf(snapshot.GetProperty("user")));
        Assert.Equal(PlanChangeOutcome.UserNotFound, await app.SetPlanAsync(Guid.NewGuid(), UserPlan.Plus));
    }

    [Fact]
    public async Task Expired_plan_gives_no_features()
    {
        var user = await app.CreateAiUserAsync();
        // Shorter than the access token lifetime, so the request is still authenticated.
        await app.SetPlanAsync(user.Id, UserPlan.Plus, app.Time.GetUtcNow().AddMinutes(1));

        var before = await CreateDraftJobAsync(user);
        app.Time.Advance(TimeSpan.FromMinutes(1));
        var after = await CreateDraftJobAsync(user);

        Assert.Equal(HttpStatusCode.Accepted, before.StatusCode);
        Assert.Equal("feature_not_available", (await after.ReadJsonAsync()).Code());
        Assert.Empty(FeaturesOf(await MeAsync(user)));
    }

    [Fact]
    public async Task Losing_access_keeps_started_jobs_available()
    {
        app.Llm.Respond = _ => OneDraft;
        var user = await app.CreateAiUserAsync();
        var jobId = (await (await CreateDraftJobAsync(user)).ReadJsonAsync()).GetProperty("id").GetGuid();
        await app.SetPlanAsync(user.Id, UserPlan.Standard);
        await ProcessOutboxAsync();

        var job = await (await user.Client.GetAsync($"/v1/ai/node-drafts/{jobId}")).ReadJsonAsync();
        var confirmed = await user.Client.PostJsonAsync($"/v1/ai/node-drafts/{jobId}/confirm",
            new { nodes = new[] { OneTime(Guid.NewGuid(), "Купить хлеб") } }, Key());

        Assert.Equal("ready", job.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
    }

    [Fact]
    public async Task Continuations_are_requested_only_for_users_with_the_feature_and_consent()
    {
        var plain = await app.CreateUserAsync();
        var noConsent = await app.CreateUserAsync();
        await app.SetPlanAsync(noConsent.Id, UserPlan.Plus);
        var allowed = await app.CreateAiUserAsync();
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await plain.Client.PostJsonAsync("/v1/nodes", OneTime(a, "Записаться к врачу"), Key());
        await noConsent.Client.PostJsonAsync("/v1/nodes", OneTime(b, "Записаться к врачу"), Key());
        await allowed.Client.PostJsonAsync("/v1/nodes", OneTime(c, "Записаться к врачу"), Key());
        await noConsent.Client.SendJsonAsync(HttpMethod.Patch, $"/v1/nodes/{b}",
            new { expected_version = 1, title = "Записаться к стоматологу", timezone = "Europe/Moscow" }, Key());

        Assert.Equal((0, 0, 1), (await ContinuationRequestsAsync(a), await ContinuationRequestsAsync(b), await ContinuationRequestsAsync(c)));
    }

    [Fact]
    public async Task Consent_withdrawn_during_the_debounce_stops_the_pipeline()
    {
        var user = await app.CreateAiUserAsync();
        var source = Guid.NewGuid();
        var llmCalls = app.Llm.Requests.Count;
        await user.Client.PostJsonAsync("/v1/nodes", OneTime(source, "Подать документы на визу"), Key());
        await ConsentAsync(user, false);

        await ProcessOutboxAsync();

        Assert.Equal(1, await ContinuationRequestsAsync(source));
        Assert.False(await app.WithDbAsync(db => db.NodeSuggestions.AnyAsync(s => s.SourceNodeId == source)));
        Assert.Equal(llmCalls, app.Llm.Requests.Count);
    }

    [Fact]
    public async Task Ai_import_needs_the_feature_but_nodus_files_do_not()
    {
        var user = await app.CreateUserAsync();
        var nodusFile = JsonSerializer.Serialize(new
        {
            source = "nodus", version = 1,
            nodes = new[] { new { type = "one_time", title = "Из файла", timezone = "Europe/Moscow" } }
        });

        var foreign = await user.Client.PostJsonAsync("/v1/me/import", new { content = "Task\nСдать отчёт\n", file_name = "a.csv" }, Key());
        var direct = await user.Client.PostJsonAsync("/v1/me/import", new { content = nodusFile }, Key());
        var problem = await foreign.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(("feature_not_available", "ai_import"), (problem.Code(), problem.GetProperty("feature").GetString()));
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.False(await app.WithDbAsync(db => db.ImportJobs.AnyAsync(j => j.UserId == user.Id)));
    }

    [Fact]
    public async Task Plan_command_validates_arguments_and_lists_plus_users()
    {
        var user = await app.CreateUserAsync();

        async Task<(int Code, string Output)> RunAsync(params string[] args)
        {
            var output = new StringWriter();
            var code = await PlanCommand.RunAsync(app.Services, args, output, CancellationToken.None);
            return (code, output.ToString());
        }

        var set = await RunAsync("set", "--user-id", user.Id.ToString(), "--plan", "plus", "--expires", "2999-01-01T00:00:00Z");
        var list = await RunAsync("list");
        var missing = await RunAsync("set", "--email", "nobody@example.com", "--plan", "plus");

        Assert.Equal(0, set.Code);
        Assert.Contains(user.Id.ToString(), list.Output);
        Assert.Contains("until 2999-01-01", list.Output);
        Assert.Equal(1, missing.Code);
        Assert.Equal(2, (await RunAsync("set", "--user-id", user.Id.ToString(), "--plan", "gold")).Code);
        Assert.Equal(2, (await RunAsync("set", "--user-id", user.Id.ToString(), "--email", "a@b.cd", "--plan", "plus")).Code);
        Assert.Equal(2, (await RunAsync("set", "--user-id", user.Id.ToString(), "--plan", "standard", "--expires", "2999-01-01")).Code);
        Assert.Equal(2, (await RunAsync("set", "--user-id", user.Id.ToString(), "--plan", "plus", "--expires", "2000-01-01")).Code);
        Assert.Equal(2, (await RunAsync("set", "--user-id")).Code);
        Assert.Equal(2, (await RunAsync("unknown")).Code);
    }
}
