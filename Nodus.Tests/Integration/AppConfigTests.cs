using System.Net;
using System.Text.Json;
using Nodus.Api.AppConfig;

namespace Nodus.Tests.Integration;

public class AppConfigTests : IAsyncLifetime
{
    // Default cache on: the in-process command invalidates it, as a separate command process relies on CacheSeconds.
    private readonly TestApp _app = new();

    public Task InitializeAsync() => _app.InitializeAsync();
    public Task DisposeAsync() => _app.DisposeAsync();

    private async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var code = await AppConfigCommand.RunAsync(_app.Services, args, output, CancellationToken.None);
        return (code, output.ToString());
    }

    private async Task<JsonElement> GetConfigAsync() => await (await _app.CreateClient().GetAsync("/v1/app-config")).ReadJsonAsync();

    [Fact]
    public async Task Config_is_public_and_carries_server_limits_even_when_empty()
    {
        var response = await _app.CreateClient().GetAsync("/v1/app-config");
        var body = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("values").EnumerateObject());
        Assert.Equal(4000, body.GetProperty("limits").GetProperty("ai_draft_text_max_chars").GetInt32());
        Assert.Equal(60, body.GetProperty("limits").GetProperty("display_name_max_chars").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("updated_at").ValueKind);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Command_changes_are_served_without_a_restart_with_typed_values()
    {
        await RunAsync("set", "links.privacy_policy", "https://nodus.app/privacy");
        await RunAsync("set", "min_supported_version", "1.2.0");
        await RunAsync("set", "features.import_enabled", "false", "--json");
        await RunAsync("set", "support", """{"email":"help@nodus.app"}""", "--json");

        var values = (await GetConfigAsync()).GetProperty("values");

        Assert.Equal("https://nodus.app/privacy", values.GetProperty("links.privacy_policy").GetString());
        Assert.Equal("1.2.0", values.GetProperty("min_supported_version").GetString());
        Assert.False(values.GetProperty("features.import_enabled").GetBoolean());
        Assert.Equal("help@nodus.app", values.GetProperty("support").GetProperty("email").GetString());
    }

    [Fact]
    public async Task ETag_revalidation_returns_304_until_a_value_changes()
    {
        await RunAsync("set", "links.terms", "https://nodus.app/terms");
        var first = await _app.CreateClient().GetAsync("/v1/app-config");
        var etag = first.Headers.ETag!.Tag;

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/v1/app-config");
        revalidate.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var notModified = await _app.CreateClient().SendAsync(revalidate);
        var unchanged = await RunAsync("set", "links.terms", "https://nodus.app/terms");
        await RunAsync("set", "links.terms", "https://nodus.app/terms-v2");
        using var afterChange = new HttpRequestMessage(HttpMethod.Get, "/v1/app-config");
        afterChange.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var changed = await _app.CreateClient().SendAsync(afterChange);

        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Contains("already set", unchanged.Output);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Command_gets_lists_deletes_and_rejects_bad_input()
    {
        await RunAsync("set", "links.faq", "https://nodus.app/faq");

        var get = await RunAsync("get", "links.faq");
        var list = await RunAsync("list");
        var deleted = await RunAsync("delete", "links.faq");

        Assert.Equal((0, "\"https://nodus.app/faq\""), (get.Code, get.Output.Trim()));
        Assert.Contains("links.faq", list.Output);
        Assert.Equal(0, deleted.Code);
        Assert.Equal(1, (await RunAsync("get", "links.faq")).Code);
        Assert.Equal(1, (await RunAsync("delete", "links.faq")).Code);
        Assert.False((await GetConfigAsync()).GetProperty("values").TryGetProperty("links.faq", out _));
        Assert.Equal(2, (await RunAsync("set", "Links.FAQ", "x")).Code);
        Assert.Equal(2, (await RunAsync("set", "links..faq", "x")).Code);
        Assert.Equal(2, (await RunAsync("set", "links.faq", "{not json", "--json")).Code);
        Assert.Equal(2, (await RunAsync("set", "links.faq", new string('x', AppConfigService.MaxValueBytes + 1))).Code);
        Assert.Equal(2, (await RunAsync("set", "links.faq", "x", "--yaml")).Code);
        Assert.Equal(2, (await RunAsync("unknown")).Code);
    }
}
