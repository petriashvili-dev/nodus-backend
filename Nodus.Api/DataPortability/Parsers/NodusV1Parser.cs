using System.Text.Json;
using Nodus.Api.Common;
using Nodus.Api.Nodes;
using Nodus.Api.Recurrence;

namespace Nodus.Api.DataPortability.Parsers;

/// <summary>
/// Reads <c>nodus</c> v1 tolerantly and fills defaults instead of rejecting the file:
/// <list type="bullet">
/// <item><c>type</c>: <c>recurring</c> when a rule is present, otherwise <c>one_time</c>;</item>
/// <item><c>timezone</c>: node → document → user's current zone → UTC;</item>
/// <item>rule: <c>interval</c> 1, <c>start_date</c> today, no times (all-day), no week days (weekday of start);</item>
/// <item><c>track_years</c>: true for anniversaries.</item>
/// </list>
/// Only a missing title or an unusable value of a required field makes a single node invalid; the rest is imported.
/// </summary>
public sealed class NodusV1Parser : IImportFormatParser
{
    public ImportFormat Format => new(ImportFormat.NodusSource, 1);

    public ImportParseResult Parse(JsonElement document, ImportDefaults defaults)
    {
        var candidates = new List<ImportCandidate>();
        var issues = new List<ImportIssue>();
        if (!document.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return new ImportParseResult(candidates, [new ImportIssue(-1, null, "no_nodes", "The file has no 'nodes' array.")]);

        var documentZone = Zone(document.StringOrNull("timezone")) ?? defaults.Timezone;
        var index = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            var i = index++;
            if (node.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ImportIssue(i, null, "invalid_node", "The element is not an object."));
                continue;
            }

            var title = node.StringOrNull("title")?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                issues.Add(new ImportIssue(i, null, "missing_title", "The node has no title."));
                continue;
            }

            var timezone = Zone(node.StringOrNull("timezone")) ?? documentZone;
            var today = ZonedTime.LocalDate(defaults.Now, ZonedTime.FindZone(timezone));
            var rule = node.ObjectOrNull("recurrence") is { } r ? RecurrenceRuleReader.Read(r, today) : null;
            var type = node.StringOrNull("type") switch
            {
                "one_time" => NodeType.OneTime,
                "recurring" => NodeType.Recurring,
                "anniversary" => NodeType.Anniversary,
                _ => rule is not null ? NodeType.Recurring : NodeType.OneTime
            };

            candidates.Add(new ImportCandidate(i, type, new NodeTemplateInput
            {
                Title = title,
                Description = node.StringOrNull("description"),
                Timezone = timezone,
                Date = TolerantJson.ParseDate(node.StringOrNull("date")),
                Time = TolerantJson.ParseTime(node.StringOrNull("time")),
                Recurrence = type == NodeType.Recurring ? rule : null,
                TrackYears = type == NodeType.Anniversary ? node.BoolOrNull("track_years") ?? true : null
            }));
        }

        return new ImportParseResult(candidates, issues);
    }

    private static string? Zone(string? id) => ZonedTime.TryFindZone(id, out _) ? id : null;
}
