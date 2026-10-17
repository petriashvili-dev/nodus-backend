using Nodus.Api.Nodes;
using Nodus.Api.Occurrences;
using Nodus.Api.Recurrence;

namespace Nodus.Tests.Recurrence;

public class RecurrenceProjectionTests
{
    private const string Zone = "Europe/Moscow"; // UTC+3, no DST
    private readonly RecurrenceProjectionService _projection = new(new RecurrenceCalculator());
    private readonly Node _node = new()
    {
        Id = Guid.NewGuid(), Type = NodeType.Recurring, Title = "Зарядка", Timezone = Zone
    };
    private readonly List<NodeRevision> _revisions = [];
    private readonly Dictionary<string, NodeOccurrenceState> _states = [];

    private static DateTimeOffset At(string local) => DateTimeOffset.Parse(local + "+03:00");

    private NodeHistory History => new(_node, _revisions.OrderBy(r => r.Revision).ToList(), _states);

    private void Revise(string? effectiveFrom, RecurrenceRule rule, string title = "Зарядка", int generation = 0) =>
        _revisions.Add(new NodeRevision
        {
            NodeId = _node.Id, Revision = _revisions.Count + 1,
            EffectiveFrom = effectiveFrom is null ? null : At(effectiveFrom),
            Title = title, Timezone = Zone, Recurrence = rule.Normalize(), Generation = generation
        });

    private static RecurrenceRule Daily(int hour, int minute = 0) => new()
    {
        Frequency = RecurrenceFrequency.Daily, StartDate = new(2026, 10, 1), Times = [new(hour, minute)]
    };

    [Fact]
    public void Editing_title_and_time_keeps_past_occurrences_unchanged()
    {
        Revise(null, Daily(9));
        var before = _projection.Project(History, new(2026, 10, 10), new(2026, 10, 16), At("2026-10-17T10:00"));

        Revise("2026-10-17T10:00", Daily(8), "Утренняя зарядка");
        var after = _projection.Project(History, new(2026, 10, 10), new(2026, 10, 19), At("2026-10-17T10:05"));

        Assert.Equal(before.Select(o => (o.Key, o.Title, o.ScheduledAt)),
            after.Where(o => o.Date < new DateOnly(2026, 10, 17)).Select(o => (o.Key, o.Title, o.ScheduledAt)));
        var today = Assert.Single(after, o => o.Date == new DateOnly(2026, 10, 17));
        Assert.Equal(("Зарядка", At("2026-10-17T09:00")), (today.Title, today.ScheduledAt!.Value));
        var tomorrow = Assert.Single(after, o => o.Date == new DateOnly(2026, 10, 18));
        Assert.Equal(("Утренняя зарядка", At("2026-10-18T08:00")), (tomorrow.Title, tomorrow.ScheduledAt!.Value));
    }

    [Fact]
    public void Edit_during_the_day_applies_to_todays_occurrence_that_is_not_yet_past()
    {
        Revise(null, Daily(20));
        Revise("2026-10-17T10:00", Daily(9), "Вечер → утро");

        var today = Assert.Single(_projection.Project(History, new(2026, 10, 17), new(2026, 10, 17), At("2026-10-17T10:00")));

        Assert.Equal(("Вечер → утро", At("2026-10-17T09:00"), true), (today.Title, today.ScheduledAt!.Value, today.IsPast));
        Assert.Equal(OccurrenceKey.For(0, new(2026, 10, 17), 0), today.Key);
    }

    [Fact]
    public void Adding_a_weekday_does_not_invent_past_occurrences()
    {
        var mondays = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly, StartDate = new(2026, 10, 5), WeekDays = [DayOfWeek.Monday]
        };
        Revise(null, mondays);
        Revise("2026-10-17T12:00", mondays with { WeekDays = [DayOfWeek.Monday, DayOfWeek.Tuesday] });

        var dates = _projection.Project(History, new(2026, 10, 5), new(2026, 10, 21), At("2026-10-17T12:00"))
            .Select(o => o.Date!.Value).ToList();

        Assert.Equal([new(2026, 10, 5), new(2026, 10, 12), new(2026, 10, 19), new(2026, 10, 20)], dates);
    }

    [Fact]
    public void Ending_the_series_keeps_history_and_states()
    {
        Revise(null, Daily(9));
        var key = OccurrenceKey.For(0, new(2026, 10, 15), 0);
        _states[key] = new NodeOccurrenceState
        {
            NodeId = _node.Id, OccurrenceKey = key, State = OccurrenceState.Untied, Title = "Зарядка",
            CompletedAt = At("2026-10-15T09:10")
        };
        Revise("2026-10-17T10:00", Daily(9) with { Until = new(2026, 10, 16) });

        var occurrences = _projection.Project(History, new(2026, 10, 14), new(2026, 10, 20), At("2026-10-17T10:00"));

        Assert.Equal([new(2026, 10, 14), new(2026, 10, 15), new(2026, 10, 16), new(2026, 10, 17)],
            occurrences.Select(o => o.Date!.Value));
        Assert.Equal(OccurrenceState.Untied, occurrences.Single(o => o.Key == key).State);
    }

    [Fact]
    public void Changing_frequency_starts_a_new_generation_of_keys()
    {
        Revise(null, Daily(9));
        Revise("2026-10-17T10:00", new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly, StartDate = new(2026, 10, 1), WeekDays = [DayOfWeek.Monday],
            Times = [new(9, 0)]
        }, generation: 1);

        var keys = _projection.Project(History, new(2026, 10, 16), new(2026, 10, 19), At("2026-10-17T10:00"))
            .Select(o => o.Key).ToList();

        Assert.Equal([OccurrenceKey.For(0, new(2026, 10, 16), 0), OccurrenceKey.For(0, new(2026, 10, 17), 0),
            OccurrenceKey.For(1, new(2026, 10, 19), 0)], keys);
    }

    [Fact]
    public void Highlights_show_today_and_nearest_next_or_only_nearest_future()
    {
        Revise(null, new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly, StartDate = new(2026, 10, 1),
            WeekDays = [DayOfWeek.Saturday, DayOfWeek.Wednesday], Times = [new(9, 0)]
        });

        var saturday = _projection.Highlights(History, At("2026-10-17T12:00")).Select(o => o.Date!.Value);
        var sunday = _projection.Highlights(History, At("2026-10-18T12:00")).Select(o => o.Date!.Value);

        Assert.Equal([new(2026, 10, 17), new(2026, 10, 21)], saturday);
        Assert.Equal([new DateOnly(2026, 10, 21)], sunday);
    }

    [Fact]
    public void Rare_yearly_series_finds_next_occurrence_beyond_one_window()
    {
        _node.Type = NodeType.Anniversary;
        _revisions.Add(new NodeRevision
        {
            NodeId = _node.Id, Revision = 1, Title = "День рождения", Timezone = Zone, TrackYears = true,
            Recurrence = new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly, StartDate = new(1990, 3, 1) }
        });

        var next = Assert.Single(_projection.Highlights(History, At("2026-10-17T12:00")));

        Assert.Equal((new DateOnly(2027, 3, 1), 37, (DateTimeOffset?)null), (next.Date!.Value, next.Years!.Value, next.ScheduledAt));
    }

    [Fact]
    public void Find_resolves_keys_through_the_governing_revision()
    {
        Revise(null, Daily(9));
        Revise("2026-10-17T10:00", Daily(8), "Новая");

        var past = _projection.Find(History, OccurrenceKey.For(0, new(2026, 10, 16), 0), At("2026-10-17T10:00"));
        var future = _projection.Find(History, OccurrenceKey.For(0, new(2026, 10, 20), 0), At("2026-10-17T10:00"));
        var missing = _projection.Find(History, OccurrenceKey.For(0, new(2026, 10, 20), 1), At("2026-10-17T10:00"));

        Assert.Equal((1, "Зарядка"), (past!.Revision, past.Title));
        Assert.Equal((2, "Новая"), (future!.Revision, future.Title));
        Assert.Null(missing);
    }
}
