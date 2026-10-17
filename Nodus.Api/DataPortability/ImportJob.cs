namespace Nodus.Api.DataPortability;

public enum ImportJobStatus
{
    Queued,
    Processing,

    /// <summary>The foreign file was converted; the preview waits for the user's confirmation.</summary>
    Ready,
    Completed,
    Failed,
    Cancelled,
    Expired
}

/// <summary>
/// Import of a foreign (non-Nodus) file through AI conversion. Nodus files are imported synchronously and need no job.
/// </summary>
public sealed class ImportJob
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ImportJobStatus Status { get; set; }
    public string? FileName { get; set; }

    /// <summary>The uploaded text; erased as soon as the conversion finishes.</summary>
    public string? InputText { get; set; }

    /// <summary>Default zone for nodes the file does not place in a zone.</summary>
    public required string Timezone { get; set; }

    /// <summary>The converted document in the current Nodus format (JSON).</summary>
    public string? Preview { get; set; }

    /// <summary><see cref="ImportResult"/> as JSON once applied.</summary>
    public string? Result { get; set; }

    public string? LastError { get; set; }
    public int Attempts { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
