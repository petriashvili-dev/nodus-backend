using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nodus.Api.Devices;

namespace Nodus.Api.Infrastructure.Apns;

/// <summary>Alert push. Texts must be safe for the lock screen: no node content, no dictated speech.</summary>
public sealed record ApnsAlert(
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    string? CollapseId = null,
    string? ThreadId = null);

public enum ApnsOutcome
{
    Sent,

    /// <summary>The token is dead (unregistered, bad, wrong topic): remove it.</summary>
    InvalidToken,

    /// <summary>Temporary failure after retries; the job may retry later.</summary>
    Retryable,

    /// <summary>Configuration or payload problem; retrying will not help.</summary>
    Failed
}

public sealed record ApnsResult(ApnsOutcome Outcome, string? Reason = null, ApnsEnvironment? Environment = null);

public interface IApnsClient
{
    Task<ApnsResult> SendAsync(string deviceToken, ApnsEnvironment environment, ApnsAlert alert, CancellationToken ct);
}

public sealed class ApnsClient(
    HttpClient http,
    ApnsTokenProvider tokens,
    IOptions<ApnsOptions> options,
    TimeProvider time,
    ILogger<ApnsClient> logger) : IApnsClient
{
    private const string SandboxHost = "https://api.sandbox.push.apple.com";
    private const string ProductionHost = "https://api.push.apple.com";
    private const int MaxAttempts = 3;

    public async Task<ApnsResult> SendAsync(string deviceToken, ApnsEnvironment environment, ApnsAlert alert, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured) return new ApnsResult(ApnsOutcome.Failed, "not_configured");
        try
        {
            tokens.GetToken();
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            logger.LogError("Apns:PrivateKey is not a valid .p8 key");
            return new ApnsResult(ApnsOutcome.Failed, "invalid_private_key");
        }

        var payload = JsonSerializer.Serialize(BuildPayload(alert));
        var environments = environment switch
        {
            ApnsEnvironment.Production => [ApnsEnvironment.Production],
            ApnsEnvironment.Sandbox => [ApnsEnvironment.Sandbox],
            _ => o.PreferSandbox
                ? new[] { ApnsEnvironment.Sandbox, ApnsEnvironment.Production }
                : [ApnsEnvironment.Production, ApnsEnvironment.Sandbox]
        };

        ApnsResult result = new(ApnsOutcome.Failed, "no_environment");
        foreach (var candidate in environments)
        {
            result = await SendToAsync(candidate, deviceToken, payload, alert, ct);
            // With an unknown environment a "bad token" may just mean "the other gateway".
            if (result.Outcome != ApnsOutcome.InvalidToken || environment != ApnsEnvironment.Unknown) return result;
        }

        return result;
    }

    private async Task<ApnsResult> SendToAsync(ApnsEnvironment environment, string deviceToken, string payload,
        ApnsAlert alert, CancellationToken ct)
    {
        var url = $"{(environment == ApnsEnvironment.Production ? ProductionHost : SandboxHost)}/3/device/{deviceToken}";
        for (var attempt = 1; ; attempt++)
        {
            string? reason;
            HttpStatusCode status;
            var token = tokens.GetToken();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
                request.Headers.Add("apns-topic", options.Value.BundleId);
                request.Headers.Add("apns-push-type", "alert");
                request.Headers.Add("apns-priority", "10");
                request.Headers.Add("apns-expiration", time.GetUtcNow().AddHours(6).ToUnixTimeSeconds().ToString());
                if (alert.CollapseId is { Length: > 0 } collapse)
                    request.Headers.Add("apns-collapse-id", collapse[..Math.Min(64, collapse.Length)]);

                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode) return new ApnsResult(ApnsOutcome.Sent, Environment: environment);
                status = response.StatusCode;
                reason = ReadReason(await response.Content.ReadAsStringAsync(ct));
            }
            catch (HttpRequestException e)
            {
                logger.LogWarning(e, "APNs request failed (attempt {Attempt})", attempt);
                if (attempt >= MaxAttempts) return new ApnsResult(ApnsOutcome.Retryable, "network");
                await DelayAsync(attempt, ct);
                continue;
            }

            logger.LogWarning("APNs rejected push: {Status} {Reason}, env {Environment}, token {TokenPrefix}",
                (int)status, reason, environment, deviceToken[..Math.Min(8, deviceToken.Length)]);

            if (status == HttpStatusCode.Gone || reason is "Unregistered" or "BadDeviceToken" or "DeviceTokenNotForTopic")
                return new ApnsResult(ApnsOutcome.InvalidToken, reason, environment);

            if (reason == "ExpiredProviderToken" && attempt < MaxAttempts)
            {
                tokens.Invalidate(token);
                continue;
            }

            // TooManyProviderTokenUpdates is a limit on token refreshes; hammering only makes it worse.
            var transient = status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                                or HttpStatusCode.ServiceUnavailable
                            && reason != "TooManyProviderTokenUpdates";
            if (!transient) return new ApnsResult(ApnsOutcome.Failed, reason ?? ((int)status).ToString());
            if (attempt >= MaxAttempts) return new ApnsResult(ApnsOutcome.Retryable, reason);
            await DelayAsync(attempt, ct);
        }
    }

    private static Dictionary<string, object> BuildPayload(ApnsAlert alert)
    {
        var aps = new Dictionary<string, object>
        {
            ["alert"] = new Dictionary<string, string> { ["title"] = alert.Title, ["body"] = alert.Body },
            ["sound"] = "default"
        };
        if (alert.ThreadId is not null) aps["thread-id"] = alert.ThreadId;
        return new Dictionary<string, object> { ["aps"] = aps, ["nodus"] = alert.Data };
    }

    private static string? ReadReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task DelayAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt) * (0.8 + Random.Shared.NextDouble() * 0.4)), ct);
}
