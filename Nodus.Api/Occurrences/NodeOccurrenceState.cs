namespace Nodus.Api.Occurrences;

public enum OccurrenceState
{
    Tied,
    Untied
}

/// <summary>
/// Explicit state of one occurrence (one-time nodes use <see cref="OccurrenceKey.Once"/>).
/// Rows are never removed by template edits; a retie keeps the row with <see cref="OccurrenceState.Tied"/>.
/// </summary>
public sealed class NodeOccurrenceState
{
    public Guid NodeId { get; set; }
    public required string OccurrenceKey { get; set; }
    public OccurrenceState State { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public long Version { get; set; }

    // Snapshot of the occurrence as it was when the state was last recorded.
    public int Revision { get; set; }
    public DateOnly? Date { get; set; }
    public TimeOnly? Time { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public required string Title { get; set; }
}
