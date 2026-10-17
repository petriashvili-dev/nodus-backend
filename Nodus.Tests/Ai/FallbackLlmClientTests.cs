using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nodus.Api.Infrastructure.Llm;
using Nodus.Tests.Integration;

namespace Nodus.Tests.Ai;

public class FallbackLlmClientTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
    private readonly List<(string Host, JsonElement Body)> _calls = [];
    private readonly Dictionary<string, Func<HttpStatusCode>> _status = new()
    {
        ["neuroapi.host"] = () => HttpStatusCode.OK,
        ["api.timeweb.ai"] = () => HttpStatusCode.OK
    };

    private static readonly LlmRequest Request = new("test", [LlmMessage.User("hi")],
        new LlmJsonSchema("s", JsonDocument.Parse("""{"type":"object"}""").RootElement));

    private FallbackLlmClient Client(int failureThreshold = 5) => new(
        new StubFactory(new Handler(this)),
        Microsoft.Extensions.Options.Options.Create(new LlmOptions
        {
            CircuitFailureThreshold = failureThreshold,
            Providers =
            [
                new LlmProviderOptions
                {
                    Name = "timeweb", Priority = 2, BaseUrl = "https://api.timeweb.ai", ApiKey = "k2", Model = "m2", MaxAttempts = 1
                },
                new LlmProviderOptions
                {
                    Name = "neuroapi", Priority = 1, BaseUrl = "https://neuroapi.host", ApiKey = "k1", Model = "m1", MaxAttempts = 2,
                    SupportsJsonSchema = true, InputCostPerMillion = 1m, OutputCostPerMillion = 2m
                },
                new LlmProviderOptions { Name = "unconfigured", Priority = 0, BaseUrl = "https://x", Model = "m" }
            ]
        }),
        _time, NullLogger<FallbackLlmClient>.Instance);

    [Fact]
    public async Task Primary_is_used_with_json_schema_and_cost_estimate()
    {
        var result = await Client().GenerateAsync(Request, CancellationToken.None);

        Assert.Equal(("neuroapi", "m1", """{"ok":true}"""), (result.Provider, result.Model, result.Content));
        Assert.Equal(0.00005m, result.EstimatedCost);
        var body = Assert.Single(_calls).Body;
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Server_errors_are_retried_then_fall_back_to_secondary_in_json_mode()
    {
        _status["neuroapi.host"] = () => HttpStatusCode.ServiceUnavailable;

        var result = await Client().GenerateAsync(Request, CancellationToken.None);

        Assert.Equal("timeweb", result.Provider);
        Assert.Equal(["neuroapi.host", "neuroapi.host", "api.timeweb.ai"], _calls.Select(c => c.Host));
        Assert.Equal("json_object", _calls[^1].Body.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, LlmErrorCategory.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorCategory.Unauthorized)]
    public async Task Own_payload_and_credential_errors_do_not_switch_provider(HttpStatusCode status, LlmErrorCategory category)
    {
        _status["neuroapi.host"] = () => status;

        var error = await Assert.ThrowsAsync<LlmException>(() => Client().GenerateAsync(Request, CancellationToken.None));

        Assert.Equal(category, error.Category);
        Assert.Equal(["neuroapi.host"], _calls.Select(c => c.Host));
    }

    [Fact]
    public async Task Rate_limited_primary_switches_without_retrying()
    {
        _status["neuroapi.host"] = () => HttpStatusCode.TooManyRequests;

        var result = await Client().GenerateAsync(Request, CancellationToken.None);

        Assert.Equal("timeweb", result.Provider);
        Assert.Equal(["neuroapi.host", "api.timeweb.ai"], _calls.Select(c => c.Host));
    }

    [Fact]
    public async Task Open_circuit_skips_the_primary_until_the_break_ends()
    {
        var client = Client(failureThreshold: 2);
        _status["neuroapi.host"] = () => HttpStatusCode.InternalServerError;
        await client.GenerateAsync(Request, CancellationToken.None);
        _calls.Clear();

        await client.GenerateAsync(Request, CancellationToken.None);
        Assert.Equal(["api.timeweb.ai"], _calls.Select(c => c.Host));

        _time.Advance(TimeSpan.FromSeconds(61));
        _status["neuroapi.host"] = () => HttpStatusCode.OK;
        _calls.Clear();
        Assert.Equal("neuroapi", (await client.GenerateAsync(Request, CancellationToken.None)).Provider);
    }

    [Fact]
    public async Task All_providers_failing_surfaces_the_last_transient_error()
    {
        _status["neuroapi.host"] = () => HttpStatusCode.BadGateway;
        _status["api.timeweb.ai"] = () => HttpStatusCode.ServiceUnavailable;

        var error = await Assert.ThrowsAsync<LlmException>(() => Client().GenerateAsync(Request, CancellationToken.None));

        Assert.Equal((LlmErrorCategory.ProviderError, "timeweb"), (error.Category, error.Provider));
    }

    private sealed class Handler(FallbackLlmClientTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            test._calls.Add((host, JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone()));
            var status = test._status[host]();
            var body = status == HttpStatusCode.OK
                ? """{"choices":[{"message":{"content":"```json\n{\"ok\":true}\n```"}}],"usage":{"prompt_tokens":10,"completion_tokens":20}}"""
                : """{"error":"x"}""";
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
