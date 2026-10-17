using System.Globalization;
using System.Text.Json;
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

        var documentZone = Zone(String(document, "timezone")) ?? defaults.Timezone;
        var index = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            var i = index++;
            if (node.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ImportIssue(i, null, "invalid_node", "The element is not an object."));
                continue;
            }

            var title = String(node, "title")?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                issues.Add(new ImportIssue(i, null, "missing_title", "The node has no title."));
                continue;
            }

            var timezone = Zone(String(node, "timezone")) ?? documentZone;
            var today = ZonedTime.LocalDate(defaults.Now, ZonedTime.FindZone(timezone));
            var rule = node.TryGetProperty("recurrence", out var r) && r.ValueKind == JsonValueKind.Object ? Rule(r, today) : null;
            var type = String(node, "type") switch
            {
                "one_time" => NodeType.OneTime,
                "recurring" => NodeType.Recurring,
                "anniversary" => NodeType.Anniversary,
                _ => rule is not null ? NodeType.Recurring : NodeType.OneTime
            };

            candidates.Add(new ImportCandidate(i, type, new NodeTemplateInput
            {
                Title = title,
                Description = String(node, "description"),
                Timezone = timezone,
                Date = Date(node, "date"),
                Time = Time(node, "time"),
                Recurrence = type == NodeType.Recurring ? rule : null,
                TrackYears = type == NodeType.Anniversary
                    ? node.TryGetProperty("track_years", out var ty) && ty.ValueKind == JsonValueKind.False ? false : true
                    : null
            }));
        }

        return new ImportParseResult(candidates, issues);
    }

    private static RecurrenceRule? Rule(JsonElement r, DateOnly today)
    {
        var frequency = String(r, "frequency") switch
        {
            "daily" => RecurrenceFrequency.Daily,
            "weekly" => RecurrenceFrequency.Weekly,
            "monthly" => RecurrenceFrequency.Monthly,
            "yearly" => RecurrenceFrequency.Yearly,
            _ => (RecurrenceFrequency?)null
        };
        if (frequency is null) return null;

        return new RecurrenceRule
        {
            Frequency = frequency.Value,
            Interval = Int(r, "interval") is > 0 and var interval ? interval : 1,
            StartDate = Date(r, "start_date") ?? today,
            Until = Date(r, "until"),
            Times = Array(r, "times").Select(x => ParseTime(x)).Where(x => x is not null).Select(x => x!.Value).ToArray(),
            WeekDays = Array(r, "week_days")
                .Select(x => Enum.TryParse<DayOfWeek>(x, true, out var d) ? d : (DayOfWeek?)null)
                .Where(x => x is not null).Select(x => x!.Value).ToArray(),
            MonthDay = Int(r, "month_day")
        };
    }

    /// <summary>Null for absent, null or non-integer values (<c>TryGetInt32</c> throws on non-numbers).</summary>
    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string? Zone(string? id) => ZonedTime.TryFindZone(id, out _) ? id : null;

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string?> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())
            : [];

    private static DateOnly? Date(JsonElement e, string name) =>
        DateOnly.TryParseExact(String(e, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static TimeOnly? Time(JsonElement e, string name) => ParseTime(String(e, name));

    private static TimeOnly? ParseTime(string? raw) =>
        TimeOnly.TryParseExact(raw, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? new TimeOnly(t.Hour, t.Minute)
            : null;
}
