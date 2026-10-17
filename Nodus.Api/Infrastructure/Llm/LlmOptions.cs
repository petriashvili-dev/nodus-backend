namespace Nodus.Api.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>Ordered by <see cref="LlmProviderOptions.Priority"/>; providers without key or model are skipped.</summary>
    public List<LlmProviderOptions> Providers { get; init; } = [];

    /// <summary>Upper bound for one <c>GenerateAsync</c> call across all attempts and providers.</summary>
    public int TotalTimeoutSeconds { get; init; } = 150;

    /// <summary>Consecutive transient failures that open a provider's circuit.</summary>
    public int CircuitFailureThreshold { get; init; } = 5;

    public int CircuitBreakSeconds { get; init; } = 60;

    /// <summary>Log prompts and outputs. Off by default: they contain user text.</summary>
    public bool LogContent { get; init; }
}

public sealed class LlmProviderOptions
{
    /// <summary>Name for metrics, e.g. <c>neuroapi</c> or <c>timeweb</c>.</summary>
    public string Name { get; init; } = "";

    public int Priority { get; init; } = 1;

    /// <summary>OpenAI-compatible base URL; <c>/v1/chat/completions</c> is appended.</summary>
    public string BaseUrl { get; init; } = "";

    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Attempts on this provider before falling back to the next one.</summary>
    public int MaxAttempts { get; init; } = 2;

    /// <summary>Whether <c>response_format: json_schema</c> is supported; otherwise JSON mode plus server validation.</summary>
    public bool SupportsJsonSchema { get; init; }

    /// <summary>Client-side limit; exceeding it falls through to the next provider instead of queueing.</summary>
    public int MaxRequestsPerMinute { get; init; } = 60;

    /// <summary>Price per million tokens in the billing currency, for cost estimates.</summary>
    public decimal? InputCostPerMillion { get; init; }

    public decimal? OutputCostPerMillion { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) &&
                                !string.IsNullOrWhiteSpace(Model);
}
