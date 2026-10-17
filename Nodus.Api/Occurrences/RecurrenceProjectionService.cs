using Nodus.Api.Nodes;
using Nodus.Api.Recurrence;

namespace Nodus.Api.Occurrences;

/// <summary>A ready-to-display occurrence with its historical template data and state.</summary>
public sealed record Occurrence(
    Guid NodeId,
    string Key,
    int Revision,
    string Title,
    string? Description,
    DateOnly? Date,
    TimeOnly? Time,
    DateTimeOffset? ScheduledAt,
    int? Years,
    OccurrenceState State,
    DateTimeOffset? CompletedAt,
    bool IsPast);

/// <summary>Everything needed to project a node: revisions ordered by number and explicit states by key.</summary>
public sealed record NodeHistory(
    Node Node,
    IReadOnlyList<NodeRevision> Revisions,
    IReadOnlyDictionary<string, NodeOccurrenceState> States);

public interface IRecurrenceProjectionService
{
    /// <summary>Occurrences with local dates in [from, to]. Undated one-time nodes are always included.</summary>
    IReadOnlyList<Occurrence> Project(NodeHistory history, DateOnly from, DateOnly to, DateTimeOffset now);

    /// <summary>Feed rule: today's occurrences (if any) plus the nearest occurrence after today.</summary>
    IReadOnlyList<Occurrence> Highlights(NodeHistory history, DateTimeOffset now);

    Occurrence? Find(NodeHistory history, string key, DateTimeOffset now);
}

/// <summary>
/// Replays template revisions so that past occurrences keep their historical data:
/// <list type="bullet">
/// <item>revision <c>i</c> governs an occurrence if the occurrence was already in the past when revision <c>i + 1</c>
/// became effective (timed: its instant; all-day: the end of its local day); otherwise the occurrence is
/// governed by a later revision;</item>
/// <item>a later revision never creates occurrences on local dates before its own effective date, so adding a weekday
/// does not invent history, while an edit made during a day still applies to today's not-yet-past occurrences;</item>
/// <item>an occurrence key, once governed by a revision, is never produced again by later revisions.</item>
/// </list>
/// The result is deterministic for a given set of revisions, so no materialisation job is required.
/// </summary>
public sealed class RecurrenceProjectionService(IRecurrenceCalculator calculator) : IRecurrenceProjectionService
{
    private const int NextSearchYears = 10;

    public IReadOnlyList<Occurrence> Project(NodeHistory history, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        if (history.Node.Type == NodeType.OneTime)
        {
            var once = OneTime(history, now);
            return once.Date is null || (once.Date >= from && once.Date <= to) ? [once] : [];
        }

        var result = new List<Occurrence>();
        var governed = new HashSet<string>(StringComparer.Ordinal);
        var revisions = history.Revisions;

        for (var i = 0; i < revisions.Count; i++)
        {
            var revision = revisions[i];
            var zone = ZonedTime.FindZone(revision.Timezone);
            var supersededAt = i + 1 < revisions.Count ? revisions[i + 1].EffectiveFrom : null;

            var lower = i > 0 && revision.EffectiveFrom is { } effective
                ? Max(from, ZonedTime.LocalDate(effective, zone))
                : from;
            // Nothing dated after the local date of the next revision can already be past when it takes effect.
            var upper = supersededAt is { } superseded ? Min(to, ZonedTime.LocalDate(superseded, zone)) : to;

            foreach (var local in calculator.Enumerate(revision.Recurrence, lower, upper))
            {
                var key = OccurrenceKey.For(revision.Generation, local.Date, local.Slot);
                if (governed.Contains(key)) continue;

                var (scheduledAt, pastAt) = Resolve(local, zone);
                if (supersededAt is { } s && pastAt >= s) continue;

                governed.Add(key);
                history.States.TryGetValue(key, out var state);
                result.Add(new Occurrence(
                    history.Node.Id, key, revision.Revision, revision.Title, revision.Description,
                    local.Date, local.Time, scheduledAt,
                    revision.TrackYears ? local.Date.Year - revision.Recurrence.StartDate.Year : null,
                    state?.State ?? OccurrenceState.Tied, state?.CompletedAt, pastAt <= now));
            }
        }

        return result
            .OrderBy(o => o.Date)
            .ThenBy(o => o.Time.HasValue)
            .ThenBy(o => o.Time)
            .ToList();
    }

    public IReadOnlyList<Occurrence> Highlights(NodeHistory history, DateTimeOffset now)
    {
        if (history.Node.Type == NodeType.OneTime) return [OneTime(history, now)];

        var today = ZonedTime.LocalDate(now, ZonedTime.FindZone(history.Node.Timezone));
        var todays = Project(history, today, today, now);
        return Next(history, today, now) is { } next ? [.. todays, next] : todays;
    }

    public Occurrence? Find(NodeHistory history, string key, DateTimeOffset now)
    {
        if (history.Node.Type == NodeType.OneTime)
            return key == OccurrenceKey.Once ? OneTime(history, now) : null;

        return OccurrenceKey.TryParse(key, out _, out var date, out _)
            ? Project(history, date, date, now).FirstOrDefault(o => o.Key == key)
            : null;
    }

    private Occurrence? Next(NodeHistory history, DateOnly after, DateTimeOffset now)
    {
        var limit = after.AddYears(NextSearchYears);
        var window = 31;
        for (var from = after.AddDays(1); from <= limit; window = Math.Min(window * 2, 3660))
        {
            var to = Min(from.AddDays(window - 1), limit);
            if (Project(history, from, to, now).FirstOrDefault() is { } found) return found;
            from = to.AddDays(1);
        }

        return null;
    }

    private static Occurrence OneTime(NodeHistory history, DateTimeOffset now)
    {
        var node = history.Node;
        history.States.TryGetValue(OccurrenceKey.Once, out var state);
        var isPast = node.ScheduledAt is { } at
            ? at <= now
            : node.Date is { } date && ZonedTime.StartOfDay(date.AddDays(1), ZonedTime.FindZone(node.Timezone)) <= now;
        return new Occurrence(
            node.Id, OccurrenceKey.Once, 0, node.Title, node.Description, node.Date, node.Time, node.ScheduledAt, null,
            state?.State ?? OccurrenceState.Tied, state?.CompletedAt, isPast);
    }

    /// <summary>Display instant (null for all-day) and the moment the occurrence becomes past.</summary>
    private static (DateTimeOffset? ScheduledAt, DateTimeOffset PastAt) Resolve(LocalOccurrence local, TimeZoneInfo zone)
    {
        if (local.Time is { } time)
        {
            var instant = ZonedTime.ToInstant(local.Date, time, zone);
            return (instant, instant);
        }

        return (null, ZonedTime.StartOfDay(local.Date.AddDays(1), zone));
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
