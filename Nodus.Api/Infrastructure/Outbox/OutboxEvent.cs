namespace Nodus.Api.Infrastructure.Outbox;

public static class OutboxEventTypes
{
    /// <summary>New sync revisions for recipients; fans out as SSE invalidation.</summary>
    public const string SyncChanged = "sync.changed";

    /// <summary>A committed push request; enqueued as a Hangfire job on the notifications queue.</summary>
    public const string PushRequested = "push.requested";

    /// <summary>A committed AI draft job; enqueued on the ai-drafts queue.</summary>
    public const string AiDraftRequested = "ai.draft.requested";
}

/// <summary>
/// Transactional outbox entry: written in the same transaction as the data it announces and delivered
/// at least once by <see cref="OutboxDispatcher"/>. Handlers must therefore be idempotent.
/// </summary>
public sealed class OutboxEvent
{
    public long Id { get; set; }
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Exception type of the last failure; never contains user content.</summary>
    public string? LastError { get; set; }

    /// <summary>Set when delivery was abandoned after the maximum number of attempts.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    public static OutboxEvent Create(string type, string payload, DateTimeOffset now) =>
        new() { Type = type, Payload = payload, CreatedAt = now };
}
