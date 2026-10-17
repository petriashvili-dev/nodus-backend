namespace Nodus.Api.AiContinuations;

public enum SuggestionStatus
{
    /// <summary>A continuation is ready and synced to the owner's devices ahead of the untie.</summary>
    Ready,
    Accepted,
    Dismissed,

    /// <summary>The pipeline decided there is no useful continuation.</summary>
    Skipped,

    /// <summary>The source node changed (or was deleted) after the suggestion was produced.</summary>
    Stale
}

public enum SuggestionOrigin
{
    Template,
    Cache,
    Llm
}

/// <summary>"Thread continuation" for one source node (one row per node, refreshed when its content changes).</summary>
public sealed class NodeSuggestion
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SourceNodeId { get; set; }
    public long SourceVersion { get; set; }

    /// <summary>Hash of the normalised title and description the suggestion was computed for.</summary>
    public required string ContentHash { get; set; }

    public SuggestionStatus Status { get; set; }
    public SuggestionOrigin? Origin { get; set; }
    public string? ProposedTitle { get; set; }
    public string? ProposedDescription { get; set; }
    public string? PromptVersion { get; set; }

    /// <summary>Last time this row cost an LLM call; used for the per-user daily limit.</summary>
    public DateTimeOffset? LlmCalledAt { get; set; }

    /// <summary>Recorded once; a suggestion is shown at most once, also after a retie.</summary>
    public DateTimeOffset? ShownAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
    public Guid? LinkedNodeId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Shared cache of generalised continuations. The key is an HMAC of the normalised title (not reversible) and entries
/// are only written for titles without signs of personal data, so the table holds no user information.
/// A null result is a negative entry ("no continuation").
/// </summary>
public sealed class ContinuationCacheEntry
{
    public required string KeyHash { get; set; }
    public required string PromptVersion { get; set; }
    public string? ResultTitle { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public int Hits { get; set; }
}
