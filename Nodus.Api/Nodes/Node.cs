using Nodus.Api.Recurrence;

namespace Nodus.Api.Nodes;

public enum NodeType
{
    OneTime,
    Recurring,
    Anniversary
}

/// <summary>
/// Current state of a node. For recurring and anniversary nodes this is the latest template;
/// the history lives in <see cref="NodeRevision"/>.
/// </summary>
public sealed class Node
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public NodeType Type { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }

    /// <summary>IANA time zone the local date/time and recurrence are defined in.</summary>
    public required string Timezone { get; set; }

    /// <summary>One-time: scheduled local date, null for an undated node. Anniversary: the original date.</summary>
    public DateOnly? Date { get; set; }

    /// <summary>Local time; null means all-day. Recurring nodes keep their times in <see cref="Recurrence"/>.</summary>
    public TimeOnly? Time { get; set; }

    /// <summary>UTC instant of a timed one-time node.</summary>
    public DateTimeOffset? ScheduledAt { get; set; }

    /// <summary>Recurring and anniversary nodes (for anniversaries it is derived from <see cref="Date"/>).</summary>
    public RecurrenceRule? Recurrence { get; set; }

    /// <summary>Anniversary: whether to show the number of years since the original date.</summary>
    public bool TrackYears { get; set; }

    /// <summary>Series generation; changes when the recurrence frequency changes, so new occurrence keys never collide with old ones.</summary>
    public int Generation { get; set; }

    /// <summary>Latest <see cref="NodeRevision.Revision"/>; 0 for one-time nodes.</summary>
    public int Revision { get; set; }

    /// <summary>Optimistic concurrency version, incremented on every change.</summary>
    public long Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsSeries => Type is NodeType.Recurring or NodeType.Anniversary;
}

/// <summary>
/// Immutable version of a recurring/anniversary template. A revision governs the occurrences that were not yet
/// in the past when the next revision became effective (see <c>RecurrenceProjectionService</c>).
/// </summary>
public sealed class NodeRevision
{
    public Guid NodeId { get; set; }
    public int Revision { get; set; }

    /// <summary>Moment the revision took effect; null for the first revision (it has no lower bound).</summary>
    public DateTimeOffset? EffectiveFrom { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }
    public required string Timezone { get; set; }
    public required RecurrenceRule Recurrence { get; set; }
    public int Generation { get; set; }
    public bool TrackYears { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
}
