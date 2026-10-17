using System.Text.Json;
using Nodus.Api.AiContinuations;
using Nodus.Api.Common;
using Nodus.Api.Nodes;
using Nodus.Api.Recurrence;

namespace Nodus.Api.DataPortability;

/// <summary>Shared rules of export and import: what counts as an active node and when two nodes are the same.</summary>
public static class ActiveNodes
{
    /// <summary>
    /// One-time: not done and not in the past (undated ones are always active); recurring: the rule has not ended;
    /// anniversaries: always.
    /// </summary>
    public static bool IsActive(NodeType type, DateOnly? date, TimeOnly? time, RecurrenceRule? rule, string timezone,
        DateTimeOffset now, bool done = false)
    {
        var zone = ZonedTime.FindZone(timezone);
        return type switch
        {
            NodeType.OneTime when done => false,
            NodeType.OneTime => date is not { } d ||
                                (time is { } t ? ZonedTime.ToInstant(d, t, zone) : ZonedTime.StartOfDay(d.AddDays(1), zone)) > now,
            NodeType.Recurring => rule?.Until is not { } until || until >= ZonedTime.LocalDate(now, zone),
            _ => true
        };
    }

    /// <summary>
    /// Identity for de-duplication: type, normalised title, date, time and rule. Description and zone are ignored, so
    /// re-importing the same file (or one exported from another zone) adds nothing.
    /// </summary>
    public static string Fingerprint(NodeType type, string title, DateOnly? date, TimeOnly? time, RecurrenceRule? rule) =>
        string.Join('|', type, ContinuationRules.Normalize(title), date?.ToString("O"), time?.ToString("HH:mm"),
            type == NodeType.Recurring && rule is not null ? JsonSerializer.Serialize(rule.Normalize(), NodusJson.Options) : "");
}
