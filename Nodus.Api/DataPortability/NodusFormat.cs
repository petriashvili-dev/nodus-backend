using System.Text.Json;
using Nodus.Api.Nodes;
using Nodus.Api.Recurrence;

namespace Nodus.Api.DataPortability;

/// <summary>Identity of a file format: a parser is selected by exact <c>(source, version)</c>.</summary>
public readonly record struct ImportFormat(string Source, int Version)
{
    public const string NodusSource = "nodus";

    /// <summary>Version written by export and targeted by AI conversion of foreign files.</summary>
    public const int CurrentVersion = 1;

    public static readonly ImportFormat Current = new(NodusSource, CurrentVersion);

    public override string ToString() => $"{Source}/v{Version}";
}

/// <summary>Values used when the file does not say: the zone, and "now" (missing start dates become today in the node zone).</summary>
public sealed record ImportDefaults(string Timezone, DateTimeOffset Now);

/// <summary>A node read from a file, before activity filtering, de-duplication and validation.</summary>
public sealed record ImportCandidate(int Index, NodeType Type, NodeTemplateInput Input);

public sealed record ImportIssue(int Index, string? Title, string Code, string Message);

public sealed record ImportParseResult(IReadOnlyList<ImportCandidate> Candidates, IReadOnlyList<ImportIssue> Issues);

/// <summary>
/// One implementation per format version (strategy). A new export version gets a new parser next to the old ones,
/// so files produced by any released version remain importable.
/// </summary>
public interface IImportFormatParser
{
    ImportFormat Format { get; }

    ImportParseResult Parse(JsonElement document, ImportDefaults defaults);
}

/// <summary>The exported document, version 1. Written by export; read by <c>NodusV1Parser</c>.</summary>
public sealed record NodusDocumentV1(
    string Source,
    int Version,
    DateTimeOffset ExportedAt,
    string? Timezone,
    IReadOnlyList<NodusNodeV1> Nodes);

/// <param name="Date">One-time: scheduled date (optional). Anniversary: original date.</param>
/// <param name="Recurrence">Recurring only.</param>
public sealed record NodusNodeV1(
    NodeType Type,
    string Title,
    string? Description,
    string Timezone,
    DateOnly? Date,
    TimeOnly? Time,
    RecurrenceRule? Recurrence,
    bool? TrackYears);
