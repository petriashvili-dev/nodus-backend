using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Nodus.Api.Infrastructure.Llm;

/// <summary>
/// The only <see cref="ILlmClient"/> application code sees. Tries providers in priority order (primary neuroapi.host,
/// secondary Timeweb Cloud): retries with exponential backoff and jitter on transient failures, falls back to the next
/// provider on network errors, timeouts, 429/5xx, invalid responses and open circuits, and stops at once on
/// <see cref="LlmErrorCategory.BadRequest"/>/<see cref="LlmErrorCategory.Unauthorized"/>. Everything runs under one
/// overall deadline. Metrics are logged without prompts or outputs.
/// </summary>
public sealed class FallbackLlmClient : ILlmClient
{
    private readonly IHttpClientFactory _httpClients;
    private readonly IReadOnlyList<ProviderSlot> _providers;
    private readonly LlmOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FallbackLlmClient> _logger;

    public FallbackLlmClient(IHttpClientFactory httpClients, IOptions<LlmOptions> options, TimeProvider time,
        ILogger<FallbackLlmClient> logger)
    {
        _httpClients = httpClients;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _providers = _options.Providers
            .Where(p => p.IsConfigured)
            .OrderBy(p => p.Priority)
            .Select(p => new ProviderSlot(p, new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, p.MaxRequestsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                })))
            .ToList();
    }

    /// <summary>Circuit state survives requests: the client is a singleton.</summary>
    private sealed class ProviderSlot(LlmProviderOptions options, RateLimiter limiter)
    {
        public LlmProviderOptions Options { get; } = options;
        public RateLimiter Limiter { get; } = limiter;
        public int ConsecutiveFailures;
        public DateTimeOffset OpenUntil = DateTimeOffset.MinValue;
    }

    public async Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct)
    {
        if (_providers.Count == 0)
            throw new LlmException(LlmErrorCategory.NotConfigured, "none", "No LLM provider is configured.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TotalTimeoutSeconds));
        LlmException? last = null;

        foreach (var slot in _providers)
        {
            var o = slot.Options;
            // Clients come from the factory per call so handler rotation (DNS changes) keeps working.
            var provider = new OpenAiCompatibleProvider(_httpClients.CreateClient(LlmSetup.HttpClientName), o);
            if (_time.GetUtcNow() < slot.OpenUntil)
            {
                last = new LlmException(LlmErrorCategory.CircuitOpen, o.Name, "Circuit is open.");
                Log(request, o, LlmErrorCategory.CircuitOpen, TimeSpan.Zero, null);
                continue;
            }

            for (var attempt = 1; attempt <= Math.Max(1, o.MaxAttempts); attempt++)
            {
                using var lease = slot.Limiter.AttemptAcquire();
                if (!lease.IsAcquired)
                {
                    last = new LlmException(LlmErrorCategory.RateLimited, o.Name, "Client-side provider rate limit reached.");
                    Log(request, o, LlmErrorCategory.RateLimited, TimeSpan.Zero, null);
                    break;
                }

                var started = Stopwatch.GetTimestamp();
                try
                {
                    var (content, promptTokens, completionTokens) = await provider.SendAsync(request, deadline.Token);
                    var latency = Stopwatch.GetElapsedTime(started);
                    Interlocked.Exchange(ref slot.ConsecutiveFailures, 0);
                    var result = new LlmResult(content, o.Name, o.Model, promptTokens, completionTokens, latency,
                        Cost(o, promptTokens, completionTokens));
                    Log(request, o, null, latency, result);
                    return result;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    Log(request, o, LlmErrorCategory.Timeout, Stopwatch.GetElapsedTime(started), null);
                    throw new LlmException(LlmErrorCategory.Timeout, o.Name, "The overall LLM deadline was exceeded.");
                }
                catch (LlmException e)
                {
                    last = e;
                    Log(request, o, e.Category, Stopwatch.GetElapsedTime(started), null);
                    if (!e.IsTransient) throw;
                    if (Interlocked.Increment(ref slot.ConsecutiveFailures) >= _options.CircuitFailureThreshold)
                    {
                        slot.OpenUntil = _time.GetUtcNow().AddSeconds(_options.CircuitBreakSeconds);
                        _logger.LogError("LLM circuit for {Provider} opened for {Seconds}s", o.Name, _options.CircuitBreakSeconds);
                        break;
                    }

                    if (e.Category == LlmErrorCategory.RateLimited || attempt == o.MaxAttempts) break;
                    await Task.Delay(Backoff(attempt), deadline.Token);
                }
            }
        }

        throw last ?? new LlmException(LlmErrorCategory.NotConfigured, "none", "No LLM provider could be tried.");
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) * (0.5 + Random.Shared.NextDouble()));

    private static decimal? Cost(LlmProviderOptions o, int? promptTokens, int? completionTokens) =>
        o.InputCostPerMillion is null && o.OutputCostPerMillion is null
            ? null
            : ((promptTokens ?? 0) * (o.InputCostPerMillion ?? 0) + (completionTokens ?? 0) * (o.OutputCostPerMillion ?? 0)) / 1_000_000m;

    private void Log(LlmRequest request, LlmProviderOptions o, LlmErrorCategory? error, TimeSpan latency, LlmResult? result)
    {
        if (error is null)
            _logger.LogInformation(
                "LLM {Purpose} via {Provider}/{Model}: {LatencyMs} ms, {PromptTokens}+{CompletionTokens} tokens, cost {Cost}",
                request.Purpose, o.Name, o.Model, (int)latency.TotalMilliseconds, result!.PromptTokens, result.CompletionTokens,
                result.EstimatedCost);
        else
            _logger.LogWarning("LLM {Purpose} via {Provider}/{Model} failed: {Category} after {LatencyMs} ms",
                request.Purpose, o.Name, o.Model, error, (int)latency.TotalMilliseconds);

        if (_options.LogContent && result is not null)
            _logger.LogDebug("LLM {Purpose} output: {Content}", request.Purpose, result.Content);
    }
}

public static class LlmSetup
{
    public const string HttpClientName = "llm";

    public static IServiceCollection AddNodusLlm(this IServiceCollection services)
    {
        services.AddOptions<LlmOptions>().BindConfiguration(LlmOptions.Section);
        // Per-attempt timeouts are enforced by the provider; the HttpClient itself must not cut them short.
        services.AddHttpClient(HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<ILlmClient, FallbackLlmClient>();
        return services;
    }
}
