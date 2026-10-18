using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api.Common.Resolvers;
using Nodus.Api.DataPortability;
using Nodus.Api.Infrastructure.Outbox;

namespace Nodus.Tests.Integration;

public class ExportImportTests(TestApp app) : IClassFixture<TestApp>
{
    private static string Key() => Guid.NewGuid().ToString();

    private async Task<TestUser> UserWithNodesAsync()
    {
        app.Time.Set(DateTimeOffset.Parse("2026-10-18T10:00:00+03:00"));
        var user = await app.CreateUserAsync();
        await user.Client.SendJsonAsync(HttpMethod.Put, "/v1/me/timezone", new { timezone = "Europe/Moscow" }, Key());
        object[] nodes =
        [
            new { id = Guid.NewGuid(), type = "one_time", title = "Купить подарок", timezone = "Europe/Moscow", date = "2026-11-01", time = "12:00" },
            new { id = Guid.NewGuid(), type = "one_time", title = "Когда-нибудь", timezone = "Europe/Moscow" },
            new { id = Guid.NewGuid(), type = "recurring", title = "Зарядка", timezone = "Europe/Moscow",
                recurrence = new { frequency = "daily", start_date = "2026-10-01", times = new[] { "07:30" } } },
            new { id = Guid.NewGuid(), type = "anniversary", title = "ДР мамы", timezone = "Europe/Moscow", date = "1965-03-08" }
        ];
        foreach (var node in nodes) await user.Client.PostJsonAsync("/v1/nodes", node, Key());

        // Inactive: past, done, ended series, deleted.
        await user.Client.PostJsonAsync("/v1/nodes", new { id = Guid.NewGuid(), type = "one_time", title = "Прошло", timezone = "Europe/Moscow", date = "2026-10-01" }, Key());
        var done = Guid.NewGuid();
        await user.Client.PostJsonAsync("/v1/nodes", new { id = done, type = "one_time", title = "Сделано", timezone = "Europe/Moscow" }, Key());
        await user.Client.SendJsonAsync(HttpMethod.Put, $"/v1/nodes/{done}/occurrences/once/untie", null, Key());
        await user.Client.PostJsonAsync("/v1/nodes", new
        {
            id = Guid.NewGuid(), type = "recurring", title = "Курс", timezone = "Europe/Moscow",
            recurrence = new { frequency = "weekly", start_date = "2026-09-01", until = "2026-10-01" }
        }, Key());
        var deleted = Guid.NewGuid();
        await user.Client.PostJsonAsync("/v1/nodes", new { id = deleted, type = "one_time", title = "Удалено", timezone = "Europe/Moscow" }, Key());
        await user.Client.SendJsonAsync(HttpMethod.Delete, $"/v1/nodes/{deleted}", null, Key());
        return user;
    }

    private async Task<JsonElement> ImportAsync(TestUser user, string content, string? fileName = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await user.Client.PostJsonAsync("/v1/me/import", new { content, file_name = fileName }, Key());
        Assert.Equal(expected, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Export_contains_only_active_own_nodes_in_the_versioned_format()
    {
        var user = await UserWithNodesAsync();

        var response = await user.Client.GetAsync("/v1/me/export");
        var document = await response.ReadJsonAsync();

        Assert.Contains("nodus-export-2026-10-18.json", response.Content.Headers.ContentDisposition?.ToString()
                                                        ?? string.Join(",", response.Headers.GetValues("Content-Disposition")));
        Assert.Equal(("nodus", 1, "Europe/Moscow"), (document.GetProperty("source").GetString(),
            document.GetProperty("version").GetInt32(), document.GetProperty("timezone").GetString()));
        Assert.Equal(["Купить подарок", "Когда-нибудь", "Зарядка", "ДР мамы"],
            document.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("title").GetString()));
        Assert.Equal(JsonValueKind.Null, document.GetProperty("nodes")[3].GetProperty("recurrence").ValueKind);
    }

    [Fact]
    public async Task Round_trip_adds_nodes_once_and_never_overwrites()
    {
        var source = await UserWithNodesAsync();
        var file = await (await source.Client.GetAsync("/v1/me/export")).Content.ReadAsStringAsync();
        var target = await app.CreateUserAsync();
        var own = Guid.NewGuid();
        await target.Client.PostJsonAsync("/v1/nodes", new { id = own, type = "one_time", title = "Купить подарок", timezone = "Europe/Moscow", date = "2026-11-01", time = "12:00" }, Key());

        var first = (await ImportAsync(target, file)).GetProperty("result");
        var second = (await ImportAsync(target, file)).GetProperty("result");

        Assert.Equal((3, 1), (first.GetProperty("created_nodes").GetInt32(), first.GetProperty("skipped_duplicates").GetInt32()));
        Assert.Equal((0, 4), (second.GetProperty("created_nodes").GetInt32(), second.GetProperty("skipped_duplicates").GetInt32()));
        Assert.Equal(4, await app.WithDbAsync(db => db.Nodes.CountAsync(n => n.OwnerId == target.Id && n.DeletedAt == null)));
        Assert.True(await app.WithDbAsync(db => db.Nodes.AnyAsync(n => n.Id == own && n.Version == 1)));
    }

    [Fact]
    public async Task Missing_fields_get_defaults_and_bad_entries_are_reported()
    {
        app.Time.Set(DateTimeOffset.Parse("2026-10-18T10:00:00+03:00"));
        var user = await app.CreateUserAsync();
        await user.Client.SendJsonAsync(HttpMethod.Put, "/v1/me/timezone", new { timezone = "Asia/Tbilisi" }, Key());
        const string file = """
            { "source": "nodus", "version": 1, "nodes": [
              { "title": "Полить цветы", "recurrence": { "frequency": "weekly" } },
              { "title": "Позвонить", "timezone": "Not/AZone" },
              { "type": "anniversary", "title": "Годовщина" },
              { "description": "без названия" },
              { "type": "one_time", "title": "Вчерашнее", "date": "2026-10-17" }
            ] }
            """;

        var result = (await ImportAsync(user, file)).GetProperty("result");
        var nodes = await app.WithDbAsync(db => db.Nodes.Where(n => n.OwnerId == user.Id).OrderBy(n => n.Title).ToListAsync());

        Assert.Equal((2, 1), (result.GetProperty("created_nodes").GetInt32(), result.GetProperty("skipped_inactive").GetInt32()));
        Assert.Equal(["invalid_node", "missing_title"],
            result.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()).Order());
        var watering = nodes.Single(n => n.Title == "Полить цветы");
        Assert.Equal(("Asia/Tbilisi", 1, new DateOnly(2026, 10, 18)), (watering.Timezone, watering.Recurrence!.Interval, watering.Recurrence.StartDate));
        Assert.Equal("Asia/Tbilisi", nodes.Single(n => n.Title == "Позвонить").Timezone);
    }

    [Fact]
    public async Task Unknown_nodus_version_is_rejected_without_ai()
    {
        var user = await app.CreateUserAsync();
        var requests = app.Llm.Requests.Count;

        var problem = await ImportAsync(user, """{ "source": "nodus", "version": 99, "nodes": [] }""", expected: HttpStatusCode.BadRequest);

        Assert.Equal("unsupported_format_version", problem.Code());
        Assert.Equal(requests, app.Llm.Requests.Count);
    }

    [Fact]
    public async Task Foreign_file_is_converted_by_ai_into_a_preview_and_imported_on_confirmation()
    {
        app.Time.Set(DateTimeOffset.Parse("2026-10-18T10:00:00+03:00"));
        app.Llm.Respond = r => r.Purpose == "import"
            ? """
              { "timezone": "Europe/Moscow", "nodes": [
                { "type": "one_time", "title": "Сдать отчёт", "description": null, "timezone": null, "date": "2026-10-25", "time": "10:00", "recurrence": null, "track_years": null },
                { "type": "recurring", "title": "Йога", "description": null, "timezone": null, "date": null, "time": null,
                  "recurrence": { "frequency": "weekly", "interval": 1, "start_date": null, "until": null, "times": ["19:00"], "week_days": ["tuesday"], "month_day": null },
                  "track_years": null }
              ] }
              """
            : """{"nodes":[]}""";
        var user = await app.CreateAiUserAsync();
        const string csv = "Task,Due\nСдать отчёт,25.10.2026 10:00\nЙога,каждый вторник 19:00\n";

        var queued = await ImportAsync(user, csv, "todoist.csv", HttpStatusCode.Accepted);
        var jobId = queued.GetProperty("job").GetProperty("id").GetGuid();
        await app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);
        var ready = await (await user.Client.GetAsync($"/v1/me/imports/{jobId}")).ReadJsonAsync();
        Assert.Equal(0, await app.WithDbAsync(db => db.Nodes.CountAsync(n => n.OwnerId == user.Id)));

        var confirmed = await (await user.Client.PostJsonAsync($"/v1/me/imports/{jobId}/confirm", new { indexes = new[] { 1 } }, Key())).ReadJsonAsync();

        Assert.Equal(("ai", "queued"), (queued.GetProperty("mode").GetString(), queued.GetProperty("job").GetProperty("status").GetString()));
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.Equal(["Сдать отчёт", "Йога"], ready.GetProperty("preview").EnumerateArray().Select(n => n.GetProperty("title").GetString()));
        Assert.Equal("completed", confirmed.GetProperty("status").GetString());
        Assert.Equal(1, confirmed.GetProperty("result").GetProperty("created_nodes").GetInt32());
        Assert.Equal(["Йога"], await app.WithDbAsync(db => db.Nodes.Where(n => n.OwnerId == user.Id).Select(n => n.Title).ToListAsync()));
        Assert.Null(await app.WithDbAsync(db => db.ImportJobs.Where(j => j.Id == jobId).Select(j => j.InputText).SingleAsync()));
    }

    [Fact]
    public async Task Parsers_are_selected_by_source_and_version()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ISelectionResolver<ImportFormat, IImportFormatParser>>();

        Assert.IsType<Nodus.Api.DataPortability.Parsers.NodusV1Parser>(resolver.GetOrDefault(new ImportFormat("nodus", 1)));
        Assert.Null(resolver.GetOrDefault(new ImportFormat("nodus", 2)));
        Assert.Null(resolver.GetOrDefault(new ImportFormat("todoist", 1)));
    }
}
