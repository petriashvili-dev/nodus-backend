using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nodus.Api.Devices;
using Nodus.Api.Infrastructure.Apns;
using Nodus.Tests.Integration;

namespace Nodus.Tests.Notifications;

public class ApnsClientTests
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
    private readonly ApnsAlert _alert = new("Черновики готовы", "Откройте Nodus", new Dictionary<string, string> { ["job_id"] = "42" });

    private ApnsOptions Options(string? privateKey = null) => new()
    {
        KeyId = "ABC123DEFG", TeamId = "TEAM123456", BundleId = "app.nodus",
        PrivateKey = privateKey ?? _key.ExportPkcs8PrivateKeyPem()
    };

    [Fact]
    public void Provider_token_is_an_es256_jwt_signed_by_the_p8_key_and_cached()
    {
        var provider = new ApnsTokenProvider(Microsoft.Extensions.Options.Options.Create(Options()), _time);

        var token = provider.GetToken();
        var parts = token.Split('.');
        var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0])).RootElement;
        var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1])).RootElement;

        Assert.Equal(("ES256", "ABC123DEFG"), (header.GetProperty("alg").GetString(), header.GetProperty("kid").GetString()));
        Assert.Equal("TEAM123456", payload.GetProperty("iss").GetString());
        Assert.Equal(_time.GetUtcNow().ToUnixTimeSeconds(), payload.GetProperty("iat").GetInt64());
        Assert.True(_key.VerifyData(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"), Base64UrlEncoder.DecodeBytes(parts[2]),
            HashAlgorithmName.SHA256));

        _time.Advance(TimeSpan.FromMinutes(49));
        Assert.Equal(token, provider.GetToken());
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(token, provider.GetToken());
    }

    [Fact]
    public void Env_file_formatting_of_the_key_is_tolerated()
    {
        var pem = _key.ExportPkcs8PrivateKeyPem();
        var mangled = "\"" + pem.Replace("\n", "\\n") + "\"";

        Assert.Equal(_key.ExportPkcs8PrivateKey(), ApnsTokenProvider.DecodePrivateKey(mangled));
    }

    [Fact]
    public async Task Sends_alert_over_http2_with_topic_and_bearer_token()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, ""));

        var result = await Client(handler).SendAsync("abcd1234", ApnsEnvironment.Production, _alert, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(ApnsOutcome.Sent, result.Outcome);
        Assert.Equal("https://api.push.apple.com/3/device/abcd1234", request.Url);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal("app.nodus", request.Headers["apns-topic"]);
        Assert.StartsWith("bearer ", request.Headers["Authorization"]);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("Черновики готовы", body.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("42", body.GetProperty("nodus").GetProperty("job_id").GetString());
    }

    [Fact]
    public async Task Gone_token_is_reported_as_invalid()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.Gone, """{"reason":"Unregistered"}"""));

        var result = await Client(handler).SendAsync("abcd1234", ApnsEnvironment.Sandbox, _alert, CancellationToken.None);

        Assert.Equal(ApnsOutcome.InvalidToken, result.Outcome);
        Assert.Equal("https://api.sandbox.push.apple.com/3/device/abcd1234", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Unknown_environment_falls_back_to_the_other_gateway()
    {
        var handler = new StubHandler(r => r.RequestUri!.Host.Contains("sandbox")
            ? (HttpStatusCode.OK, "")
            : (HttpStatusCode.BadRequest, """{"reason":"BadDeviceToken"}"""));

        var result = await Client(handler).SendAsync("abcd1234", ApnsEnvironment.Unknown, _alert, CancellationToken.None);

        Assert.Equal((ApnsOutcome.Sent, ApnsEnvironment.Sandbox), (result.Outcome, result.Environment!.Value));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Expired_provider_token_is_refreshed_once()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1
            ? (HttpStatusCode.Forbidden, """{"reason":"ExpiredProviderToken"}""")
            : (HttpStatusCode.OK, ""));
        var client = Client(handler);

        var result = await client.SendAsync("abcd1234", ApnsEnvironment.Production, _alert, CancellationToken.None);

        Assert.Equal(ApnsOutcome.Sent, result.Outcome);
        Assert.NotEqual(handler.Requests[0].Headers["Authorization"], handler.Requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Missing_configuration_fails_without_calling_apple()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, ""));
        var options = Microsoft.Extensions.Options.Options.Create(new ApnsOptions());
        var client = new ApnsClient(new HttpClient(handler), new ApnsTokenProvider(options, _time), options, _time,
            NullLogger<ApnsClient>.Instance);

        var result = await client.SendAsync("abcd1234", ApnsEnvironment.Production, _alert, CancellationToken.None);

        Assert.Equal((ApnsOutcome.Failed, "not_configured"), (result.Outcome, result.Reason));
        Assert.Empty(handler.Requests);
    }

    private ApnsClient Client(StubHandler handler)
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options());
        return new ApnsClient(new HttpClient(handler), new ApnsTokenProvider(options, _time), options, _time,
            NullLogger<ApnsClient>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<(string Url, Version Version, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            if (request.Headers.Authorization is { } auth) headers["Authorization"] = $"{auth.Scheme} {auth.Parameter}";
            Requests.Add((request.RequestUri!.ToString(), request.Version, headers, await request.Content!.ReadAsStringAsync(ct)));
            var (status, body) = respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
