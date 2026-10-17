using System.Text.Json;

namespace Nodus.Api.Infrastructure.Llm;

public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string content) => new("system", content);
    public static LlmMessage User(string content) => new("user", content);
}

/// <summary>Structured-output contract: providers with JSON Schema support get the schema, others JSON mode.</summary>
public sealed record LlmJsonSchema(string Name, JsonElement Schema);

/// <param name="Purpose">Short tag for metrics and logs, e.g. <c>node-drafts</c>.</param>
public sealed record LlmRequest(
    string Purpose,
    IReadOnlyList<LlmMessage> Messages,
    LlmJsonSchema? Schema = null,
    double Temperature = 0.1,
    int? MaxTokens = null);

public sealed record LlmResult(
    string Content,
    string Provider,
    string Model,
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan Latency,
    decimal? EstimatedCost);

public interface ILlmClient
{
    Task<LlmResult> GenerateAsync(LlmRequest request, CancellationToken ct);
}

public enum LlmErrorCategory
{
    Network,
    Timeout,
    RateLimited,
    ProviderError,
    CircuitOpen,

    /// <summary>The provider rejected our payload (4xx): another provider would most likely reject it too.</summary>
    BadRequest,

    /// <summary>Wrong or missing credentials: an operator problem, not something to paper over by switching.</summary>
    Unauthorized,

    InvalidResponse,
    NotConfigured
}

public sealed class LlmException(LlmErrorCategory category, string provider, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public LlmErrorCategory Category { get; } = category;
    public string Provider { get; } = provider;

    /// <summary>Failures for which retrying or switching provider makes sense.</summary>
    public bool IsTransient => Category is LlmErrorCategory.Network or LlmErrorCategory.Timeout or
        LlmErrorCategory.RateLimited or LlmErrorCategory.ProviderError or LlmErrorCategory.CircuitOpen or
        LlmErrorCategory.InvalidResponse;
}
