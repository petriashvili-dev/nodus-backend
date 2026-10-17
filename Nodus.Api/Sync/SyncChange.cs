namespace Nodus.Api.Sync;

public enum SyncEntityType
{
    User,
    Node,
    OccurrenceState,
    Connection,
    AiDraftJob
}

public enum SyncOperation
{
    Upsert,
    Delete
}

/// <summary>
/// One entry of a recipient's change feed. <see cref="Revision"/> is allocated from <c>users.sync_revision</c>
/// under that user's row lock, so revisions are gap-free per recipient and become visible in commit order:
/// a reader with cursor N can never miss a change committed later with a revision ≤ N.
/// The feed stores references only; <c>GET /v1/sync</c> returns the current canonical state of each entity.
/// </summary>
public sealed class SyncChange
{
    public Guid RecipientUserId { get; set; }
    public long Revision { get; set; }
    public SyncEntityType EntityType { get; set; }
    public required string EntityId { get; set; }
    public SyncOperation Operation { get; set; }
    public long EntityVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Stored outcome of a client mutation, keyed by the client's <c>Idempotency-Key</c>.</summary>
public sealed class ClientOperation
{
    public Guid UserId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
