using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api.Infrastructure.Llm;
using Nodus.Api.Infrastructure.Outbox;

namespace Nodus.Tests.Integration;

public class AiDraftTests(TestApp app) : IClassFixture<TestApp>
{
    private const string TwoNodes = """
        {"nodes":[
          {"type":"one_time","title":"Купить хлеб","description":null,"date":"2026-10-18","time":"18:00","recurrence":null,"track_years":null,"ambiguities":[]},
          {"type":"recurring","title":"Зарядка","description":null,"date":null,"time":null,
           "recurrence":{"frequency":"daily","interval":1,"start_date":"2026-10-18","until":null,"times":["07:00"],"week_days":[],"month_day":null},
           "track_years":null,"ambiguities":[]}
        ]}
        """;

    private static string Key() => Guid.NewGuid().ToString();

    private async Task<Guid> CreateJobAsync(TestUser user, string text = "Завтра купить хлеб и каждый день делать зарядку")
    {
        var response = await user.Client.PostJsonAsync("/v1/ai/node-drafts",
            new { text, timezone = "Europe/Moscow", reference_time = app.Time.GetUtcNow() }, Key());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private Task ProcessOutboxAsync() =>
        app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);

    [Fact]
    public async Task Job_becomes_ready_with_drafts_and_creates_nothing_until_confirmed()
    {
        app.Llm.Respond = _ => TwoNodes;
        var user = await app.CreateUserAsync();
        await user.Client.PostJsonAsync("/v1/devices", new { installation_id = "p", apns_token = new string('a', 64) });

        var jobId = await CreateJobAsync(user);
        await ProcessOutboxAsync(); // enqueues and (Hangfire disabled) runs the job
        await ProcessOutboxAsync(); // delivers the "ready" push
        var job = await (await user.Client.GetAsync($"/v1/ai/node-drafts/{jobId}")).ReadJsonAsync();
        var pending = await (await user.Client.GetAsync("/v1/ai/node-drafts")).ReadJsonAsync();

        Assert.Equal("ready", job.GetProperty("status").GetString());
        Assert.Equal(["Купить хлеб", "Зарядка"], job.GetProperty("drafts").EnumerateArray().Select(d => d.GetProperty("title").GetString()));
        Assert.Single(pending.GetProperty("jobs").EnumerateArray());
        Assert.Equal(0, await app.WithDbAsync(db => db.Nodes.CountAsync(n => n.OwnerId == user.Id)));
        var push = Assert.Single(app.Apns.Sent, s => s.Alert.Data.GetValueOrDefault("job_id") == jobId.ToString());
        Assert.DoesNotContain("хлеб", push.Alert.Title + push.Alert.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirm_with_nodes_is_atomic_and_idempotent()
    {
        app.Llm.Respond = _ => TwoNodes;
        var user = await app.CreateUserAsync();
        var jobId = await CreateJobAsync(user);
        await ProcessOutboxAsync();
        var nodes = new[]
        {
            new { id = Guid.NewGuid(), type = "one_time", title = "Купить хлеб", timezone = "Europe/Moscow", date = "2026-10-18" }
        };
        var key = Key();

        var confirmed = await user.Client.PostJsonAsync($"/v1/ai/node-drafts/{jobId}/confirm", new { nodes }, key);
        var retried = await user.Client.PostJsonAsync($"/v1/ai/node-drafts/{jobId}/confirm", new { nodes }, key);
        var job = await (await user.Client.GetAsync($"/v1/ai/node-drafts/{jobId}")).ReadJsonAsync();

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(await confirmed.Content.ReadAsStringAsync(), await retried.Content.ReadAsStringAsync());
        Assert.Equal("confirmed", job.GetProperty("status").GetString());
        Assert.Equal(1, await app.WithDbAsync(db => db.Nodes.CountAsync(n => n.OwnerId == user.Id)));
    }

    [Fact]
    public async Task Invalid_confirmation_leaves_the_job_ready()
    {
        app.Llm.Respond = _ => TwoNodes;
        var user = await app.CreateUserAsync();
        var jobId = await CreateJobAsync(user);
        await ProcessOutboxAsync();

        var invalid = await user.Client.PostJsonAsync($"/v1/ai/node-drafts/{jobId}/confirm",
            new { nodes = new[] { new { id = Guid.NewGuid(), type = "one_time", title = "", timezone = "Europe/Moscow" } } }, Key());
        var job = await (await user.Client.GetAsync($"/v1/ai/node-drafts/{jobId}")).ReadJsonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("ready", job.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Persistent_invalid_output_fails_after_max_attempts_and_cancelled_jobs_are_not_processed()
    {
        app.Llm.Respond = _ => "definitely not json";
        var user = await app.CreateUserAsync();
        var failing = await CreateJobAsync(user);
        for (var i = 0; i < 3; i++)
        {
            await ProcessOutboxAsync();
            await app.WithDbAsync(db => db.OutboxEvents.ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, (DateTimeOffset?)null)));
        }

        var cancelled = await CreateJobAsync(user);
        await user.Client.PostJsonAsync($"/v1/ai/node-drafts/{cancelled}/cancel", null, Key());
        var requestsBefore = app.Llm.Requests.Count;
        await ProcessOutboxAsync();

        var failed = await (await user.Client.GetAsync($"/v1/ai/node-drafts/{failing}")).ReadJsonAsync();
        Assert.Equal(("failed", "invalid_ai_response"), (failed.GetProperty("status").GetString(), failed.GetProperty("error").GetString()));
        Assert.Equal(3, await app.WithDbAsync(db => db.AiDraftJobs.Where(j => j.Id == failing).Select(j => j.Attempts).SingleAsync()));
        Assert.Equal(requestsBefore, app.Llm.Requests.Count);
    }

    [Fact]
    public async Task Ready_job_reaches_the_client_through_sync()
    {
        app.Llm.Respond = _ => TwoNodes;
        var user = await app.CreateUserAsync();
        var jobId = await CreateJobAsync(user);
        await ProcessOutboxAsync();

        var sync = await (await user.Client.GetAsync("/v1/sync?cursor=0")).ReadJsonAsync();
        var change = Assert.Single(sync.GetProperty("changes").EnumerateArray());

        Assert.Equal(("ai_draft_job", jobId.ToString()), (change.GetProperty("entity_type").GetString(), change.GetProperty("entity_id").GetString()));
        Assert.Equal(2, change.GetProperty("data").GetProperty("drafts").GetArrayLength());
    }

    [Fact]
    public async Task Requests_are_validated_and_the_text_is_not_returned()
    {
        var user = await app.CreateUserAsync();

        var invalid = await user.Client.PostJsonAsync("/v1/ai/node-drafts",
            new { text = "", timezone = "Nowhere/City", reference_time = app.Time.GetUtcNow().AddDays(-5) }, Key());
        var created = await user.Client.PostJsonAsync("/v1/ai/node-drafts",
            new { text = "секретная мысль", timezone = "Europe/Moscow", reference_time = app.Time.GetUtcNow() }, Key());

        var errors = (await invalid.ReadJsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("text", out _) && errors.TryGetProperty("timezone", out _) &&
                    errors.TryGetProperty("reference_time", out _));
        Assert.DoesNotContain("секретная", await created.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unavailable_llm_fails_the_job_without_retries()
    {
        app.Llm.Respond = _ => throw new LlmException(LlmErrorCategory.NotConfigured, "none", "No provider.");
        var user = await app.CreateUserAsync();
        var jobId = await CreateJobAsync(user);

        await ProcessOutboxAsync();

        var job = await (await user.Client.GetAsync($"/v1/ai/node-drafts/{jobId}")).ReadJsonAsync();
        Assert.Equal(("failed", "ai_unavailable"), (job.GetProperty("status").GetString(), job.GetProperty("error").GetString()));
    }
}
