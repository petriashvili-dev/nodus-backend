using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api.AiContinuations;
using Nodus.Api.Infrastructure.Outbox;

namespace Nodus.Tests.Integration;

public class ContinuationTests(TestApp app) : IClassFixture<TestApp>
{
    private static string Key() => Guid.NewGuid().ToString();

    private static object OneTime(Guid id, string title) => new { id, type = "one_time", title, timezone = "Europe/Moscow" };

    private Task ProcessOutboxAsync() =>
        app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);

    private async Task<JsonElement?> SuggestionForAsync(TestUser user, Guid nodeId)
    {
        var sync = await (await user.Client.GetAsync("/v1/sync?cursor=0&limit=1000")).ReadJsonAsync();
        return sync.GetProperty("changes").EnumerateArray()
            .Where(c => c.GetProperty("entity_type").GetString() == "suggestion" && c.GetProperty("operation").GetString() == "upsert")
            .Select(c => c.GetProperty("data"))
            .Where(d => d.GetProperty("source_node_id").GetGuid() == nodeId)
            .Cast<JsonElement?>()
            .FirstOrDefault();
    }

    [Fact]
    public async Task Template_suggestion_is_synced_ahead_shown_once_and_accepted_atomically()
    {
        var user = await app.CreateUserAsync();
        var source = Guid.NewGuid();
        await user.Client.PostJsonAsync("/v1/nodes", OneTime(source, "Записаться к врачу"), Key());
        var llmCalls = app.Llm.Requests.Count;

        await ProcessOutboxAsync();
        var suggestion = (await SuggestionForAsync(user, source))!.Value;
        var id = suggestion.GetProperty("id").GetGuid();
        var shown = await (await user.Client.PostJsonAsync($"/v1/suggestions/{id}/shown", null, Key())).ReadJsonAsync();
        app.Time.Advance(TimeSpan.FromMinutes(1));
        var shownAgain = await (await user.Client.PostJsonAsync($"/v1/suggestions/{id}/shown", null, Key())).ReadJsonAsync();
        var followUp = Guid.NewGuid();
        var accept = await user.Client.PostJsonAsync($"/v1/suggestions/{id}/accept",
            new { node = OneTime(followUp, "Визит к врачу") }, Key());
        var acceptAgain = await user.Client.PostJsonAsync($"/v1/suggestions/{id}/accept",
            new { node = OneTime(Guid.NewGuid(), "Визит к врачу") }, Key());

        Assert.Equal(("Визит к врачу", "ready"), (suggestion.GetProperty("title").GetString(), suggestion.GetProperty("status").GetString()));
        Assert.Equal(llmCalls, app.Llm.Requests.Count);
        Assert.Equal(shown.GetProperty("shown_at").GetString(), shownAgain.GetProperty("shown_at").GetString());
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal("already_accepted", (await acceptAgain.ReadJsonAsync()).Code());
        Assert.True(await app.WithDbAsync(db => db.Nodes.AnyAsync(n => n.Id == followUp)));
    }

    [Fact]
    public async Task Ordinary_tasks_are_skipped_without_a_model_call()
    {
        var user = await app.CreateUserAsync();
        var source = Guid.NewGuid();
        var llmCalls = app.Llm.Requests.Count;

        await user.Client.PostJsonAsync("/v1/nodes", OneTime(source, "Купить хлеб"), Key());
        await ProcessOutboxAsync();

        Assert.Null(await SuggestionForAsync(user, source));
        Assert.Equal(llmCalls, app.Llm.Requests.Count);
        Assert.Equal(SuggestionStatus.Skipped,
            await app.WithDbAsync(db => db.NodeSuggestions.Where(s => s.SourceNodeId == source).Select(s => s.Status).SingleAsync()));
    }

    [Fact]
    public async Task Model_results_are_cached_for_generic_titles_only()
    {
        app.Llm.Respond = _ => """{"continuation":{"title":"Получить визу"}}""";
        var first = await app.CreateUserAsync();
        var second = await app.CreateUserAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        await first.Client.PostJsonAsync("/v1/nodes", OneTime(a, "Подать документы на визу"), Key());
        await ProcessOutboxAsync();
        var calls = app.Llm.Requests.Count;
        await second.Client.PostJsonAsync("/v1/nodes", OneTime(b, "подать документы на визу."), Key());
        await ProcessOutboxAsync();

        Assert.Equal("Получить визу", (await SuggestionForAsync(second, b))!.Value.GetProperty("title").GetString());
        Assert.Equal(calls, app.Llm.Requests.Count);
        var cached = await app.WithDbAsync(db => db.ContinuationCache.ToListAsync());
        Assert.DoesNotContain(cached, c => c.KeyHash.Contains("виз"));
    }

    [Fact]
    public async Task Edit_during_processing_discards_the_late_result_and_makes_the_old_one_stale()
    {
        var user = await app.CreateUserAsync();
        var source = Guid.NewGuid();
        await user.Client.PostJsonAsync("/v1/nodes", OneTime(source, "Записаться к врачу"), Key());
        await ProcessOutboxAsync();
        var oldId = (await SuggestionForAsync(user, source))!.Value.GetProperty("id").GetGuid();

        app.Llm.Respond = _ =>
        {
            // The user edits the node while the model is thinking.
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Nodus.Api.Infrastructure.Persistence.NodusDbContext>();
            db.Nodes.Where(n => n.Id == source).ExecuteUpdate(s => s.SetProperty(n => n.Title, "Заказать пиццу на вечер"));
            return """{"continuation":{"title":"Съесть пиццу"}}""";
        };
        await user.Client.SendJsonAsync(HttpMethod.Patch, $"/v1/nodes/{source}",
            new { expected_version = 1, title = "Заказать торт", timezone = "Europe/Moscow" }, Key());
        await ProcessOutboxAsync();

        var sync = await (await user.Client.GetAsync("/v1/sync?cursor=0&limit=1000")).ReadJsonAsync();
        var last = sync.GetProperty("changes").EnumerateArray().Last(c => c.GetProperty("entity_id").GetString() == oldId.ToString());
        Assert.Equal("delete", last.GetProperty("operation").GetString());
        Assert.Equal(SuggestionStatus.Stale,
            await app.WithDbAsync(db => db.NodeSuggestions.Where(s => s.SourceNodeId == source).Select(s => s.Status).SingleAsync()));
    }

    [Fact]
    public async Task Recurring_and_completed_nodes_get_no_continuation()
    {
        var user = await app.CreateUserAsync();
        var (series, done) = (Guid.NewGuid(), Guid.NewGuid());
        await user.Client.PostJsonAsync("/v1/nodes", new
        {
            id = series, type = "recurring", title = "Записаться к врачу", timezone = "Europe/Moscow",
            recurrence = new { frequency = "monthly", start_date = "2026-10-01" }
        }, Key());
        await user.Client.PostJsonAsync("/v1/nodes", OneTime(done, "Записаться к стоматологу"), Key());
        await user.Client.SendJsonAsync(HttpMethod.Put, $"/v1/nodes/{done}/occurrences/once/untie", null, Key());

        await ProcessOutboxAsync();

        Assert.False(await app.WithDbAsync(db => db.NodeSuggestions.AnyAsync(s => s.SourceNodeId == series || s.SourceNodeId == done)));
    }
}
