using Nodus.Api.Infrastructure.Llm;

namespace Nodus.Api.AiDrafts;

public enum AiDraftJobStatus
{
    Queued,
    Processing,

    /// <summary>Drafts are ready for review. No node exists until the user confirms.</summary>
    Ready,
    Confirmed,
    Failed,
    Cancelled,
    Expired
}

/// <summary>Durable job that turns dictated text into node drafts.</summary>
public sealed class AiDraftJob : IAiJob
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AiDraftJobStatus Status { get; set; }

    /// <summary>Recognised speech; erased by retention once the job is finished.</summary>
    public string? InputText { get; set; }

    public required string Timezone { get; set; }

    /// <summary>The exact moment the user spoke, so that "tomorrow" resolves against it, not against processing time.</summary>
    public DateTimeOffset ReferenceTime { get; set; }

    public int Attempts { get; set; }

    /// <summary>Category of the last failure (never user content).</summary>
    public string? LastError { get; set; }

    public string? PromptVersion { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }

    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? InputPurgedAt { get; set; }

    /// <summary>Drafts not confirmed by then expire.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsOpen => Status is AiDraftJobStatus.Queued or AiDraftJobStatus.Processing or AiDraftJobStatus.Ready;
}

/// <summary>One structured draft. <see cref="Payload"/> is the validated <c>NodeDraftDto</c> as JSON.</summary>
public sealed class AiNodeDraft
{
    public Guid JobId { get; set; }
    public int Index { get; set; }
    public required string Payload { get; set; }
}
