using Nodus.Api.Recurrence;

namespace Nodus.Tests.Recurrence;

public class RecurrenceCalculatorTests
{
    private readonly RecurrenceCalculator _calculator = new();

    private List<DateOnly> Dates(RecurrenceRule rule, string from, string to) =>
        _calculator.Enumerate(rule, DateOnly.Parse(from), DateOnly.Parse(to)).Select(o => o.Date).Distinct().ToList();

    [Fact]
    public void Daily_with_interval_counts_from_start_date()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Interval = 3, StartDate = new(2026, 1, 1) };

        Assert.Equal([new(2026, 1, 4), new(2026, 1, 7), new(2026, 1, 10)], Dates(rule, "2026-01-02", "2026-01-10"));
    }

    [Fact]
    public void Weekly_uses_listed_days_and_interval_by_iso_week()
    {
        // 2026-01-01 is a Thursday; its ISO week starts on Monday 2025-12-29.
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Weekly, Interval = 2, StartDate = new(2026, 1, 1),
            WeekDays = [DayOfWeek.Monday, DayOfWeek.Friday]
        };

        Assert.Equal(
            [new(2026, 1, 2), new(2026, 1, 12), new(2026, 1, 16), new(2026, 1, 26), new(2026, 1, 30)],
            Dates(rule, "2026-01-01", "2026-01-31"));
    }

    [Fact]
    public void Weekly_without_days_uses_start_weekday()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, StartDate = new(2026, 10, 14) };

        Assert.Equal([new(2026, 10, 14), new(2026, 10, 21)], Dates(rule, "2026-10-01", "2026-10-27"));
    }

    [Fact]
    public void Monthly_on_31st_is_clamped_to_month_end()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, StartDate = new(2026, 1, 31) };

        Assert.Equal(
            [new(2026, 1, 31), new(2026, 2, 28), new(2026, 3, 31), new(2026, 4, 30)],
            Dates(rule, "2026-01-01", "2026-04-30"));
    }

    [Fact]
    public void Monthly_in_leap_year_uses_29_february()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, StartDate = new(2028, 1, 30) };

        Assert.Equal([new(2028, 2, 29)], Dates(rule, "2028-02-01", "2028-02-29"));
    }

    [Fact]
    public void Yearly_on_29_february_falls_back_to_28_february_in_common_years()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly, StartDate = new(2024, 2, 29) };

        Assert.Equal(
            [new(2024, 2, 29), new(2025, 2, 28), new(2026, 2, 28), new(2027, 2, 28), new(2028, 2, 29)],
            Dates(rule, "2024-01-01", "2028-12-31"));
    }

    [Fact]
    public void Until_is_inclusive_and_nothing_precedes_start()
    {
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Daily, StartDate = new(2026, 5, 10), Until = new(2026, 5, 12)
        };

        Assert.Equal([new(2026, 5, 10), new(2026, 5, 11), new(2026, 5, 12)], Dates(rule, "2026-05-01", "2026-05-31"));
    }

    [Fact]
    public void Several_times_per_day_produce_ordered_slots()
    {
        var rule = new RecurrenceRule
        {
            Frequency = RecurrenceFrequency.Daily, StartDate = new(2026, 5, 10), Times = [new(9, 0), new(21, 0)]
        };

        var occurrences = _calculator.Enumerate(rule, new(2026, 5, 10), new(2026, 5, 10)).ToList();

        Assert.Equal([new(new(2026, 5, 10), 0, new(9, 0)), new(new(2026, 5, 10), 1, new(21, 0))], occurrences);
    }

    /// <summary>
    /// Property: enumerating a range equals enumerating any split of it, results are strictly ascending
    /// and every date passes the frequency's own predicate.
    /// </summary>
    [Fact]
    public void Random_rules_are_consistent_across_range_splits()
    {
        var random = new Random(20261017);
        for (var run = 0; run < 2000; run++)
        {
            var frequency = (RecurrenceFrequency)random.Next(4);
            var start = new DateOnly(2020, 1, 1).AddDays(random.Next(3000));
            var rule = new RecurrenceRule
            {
                Frequency = frequency,
                Interval = random.Next(1, 5),
                StartDate = start,
                WeekDays = frequency == RecurrenceFrequency.Weekly
                    ? Enumerable.Range(0, 7).Where(_ => random.Next(3) == 0).Select(d => (DayOfWeek)d).ToArray()
                    : [],
                MonthDay = frequency == RecurrenceFrequency.Monthly && random.Next(2) == 0 ? random.Next(28, 32) : null
            }.Normalize();
            var from = start.AddDays(random.Next(-100, 400));
            var to = from.AddDays(random.Next(0, 800));
            var split = from.AddDays(random.Next(0, to.DayNumber - from.DayNumber + 1));

            var whole = Dates(rule, from.ToString("O"), to.ToString("O"));
            var parts = Dates(rule, from.ToString("O"), split.ToString("O"))
                .Concat(Dates(rule, split.AddDays(1).ToString("O"), to.ToString("O"))).ToList();

            Assert.Equal(whole, parts);
            Assert.True(whole.Zip(whole.Skip(1)).All(p => p.First < p.Second), $"Not ascending: {rule}");
            Assert.All(whole, d => Assert.True(Matches(rule, d), $"{d} does not match {rule}"));
        }
    }

    private static bool Matches(RecurrenceRule rule, DateOnly date)
    {
        if (date < rule.StartDate) return false;
        var s = rule.StartDate;
        switch (rule.Frequency)
        {
            case RecurrenceFrequency.Daily:
                return (date.DayNumber - s.DayNumber) % rule.Interval == 0;
            case RecurrenceFrequency.Weekly:
                var days = rule.WeekDays.Count > 0 ? rule.WeekDays : [s.DayOfWeek];
                int WeekStart(DateOnly d) => d.DayNumber - ((int)d.DayOfWeek + 6) % 7;
                return days.Contains(date.DayOfWeek) && (WeekStart(date) - WeekStart(s)) / 7 % rule.Interval == 0;
            case RecurrenceFrequency.Monthly:
                var day = Math.Min(rule.MonthDay ?? s.Day, DateTime.DaysInMonth(date.Year, date.Month));
                return date.Day == day && (date.Year * 12 + date.Month - s.Year * 12 - s.Month) % rule.Interval == 0;
            default:
                var yearly = Math.Min(s.Day, DateTime.DaysInMonth(date.Year, s.Month));
                return date.Month == s.Month && date.Day == yearly && (date.Year - s.Year) % rule.Interval == 0;
        }
    }
}
