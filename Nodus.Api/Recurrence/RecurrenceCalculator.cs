namespace Nodus.Api.Recurrence;

/// <summary>A calendar occurrence of a rule: local date, slot within the day and local time (null for all-day).</summary>
public readonly record struct LocalOccurrence(DateOnly Date, int Slot, TimeOnly? Time);

public interface IRecurrenceCalculator
{
    /// <summary>Occurrences whose local dates fall into [from, to], in chronological order.</summary>
    IEnumerable<LocalOccurrence> Enumerate(RecurrenceRule rule, DateOnly from, DateOnly to);
}

/// <summary>
/// Pure calendar arithmetic. Month ends are clamped (the 31st becomes the last day of a shorter month,
/// 29 February becomes 28 February in common years); intervals are counted from the start date.
/// </summary>
public sealed class RecurrenceCalculator : IRecurrenceCalculator
{
    public IEnumerable<LocalOccurrence> Enumerate(RecurrenceRule rule, DateOnly from, DateOnly to)
    {
        if (from < rule.StartDate) from = rule.StartDate;
        if (rule.Until is { } until && until < to) to = until;
        if (from > to) yield break;

        foreach (var date in Dates(rule, from, to))
        {
            if (rule.Times.Count == 0)
            {
                yield return new LocalOccurrence(date, 0, null);
                continue;
            }

            for (var slot = 0; slot < rule.Times.Count; slot++)
                yield return new LocalOccurrence(date, slot, rule.Times[slot]);
        }
    }

    private static IEnumerable<DateOnly> Dates(RecurrenceRule rule, DateOnly from, DateOnly to) => rule.Frequency switch
    {
        RecurrenceFrequency.Daily => Daily(rule, from, to),
        RecurrenceFrequency.Weekly => Weekly(rule, from, to),
        RecurrenceFrequency.Monthly => Monthly(rule, from, to),
        RecurrenceFrequency.Yearly => Yearly(rule, from, to),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Frequency, "Unknown frequency.")
    };

    private static IEnumerable<DateOnly> Daily(RecurrenceRule rule, DateOnly from, DateOnly to)
    {
        var first = rule.StartDate.DayNumber + CeilToMultiple(from.DayNumber - rule.StartDate.DayNumber, rule.Interval);
        for (var day = first; day <= to.DayNumber; day += rule.Interval)
            yield return DateOnly.FromDayNumber(day);
    }

    private static IEnumerable<DateOnly> Weekly(RecurrenceRule rule, DateOnly from, DateOnly to)
    {
        var offsets = (rule.WeekDays.Count > 0 ? rule.WeekDays : [rule.StartDate.DayOfWeek])
            .Select(RecurrenceRule.IsoDayIndex).Distinct().Order().ToArray();
        var anchor = WeekStart(rule.StartDate);
        var firstWeek = anchor + 7 * CeilToMultiple((WeekStart(from) - anchor) / 7, rule.Interval);

        for (var week = firstWeek; week <= to.DayNumber; week += 7 * rule.Interval)
            foreach (var offset in offsets)
            {
                var day = week + offset;
                if (day >= from.DayNumber && day <= to.DayNumber)
                    yield return DateOnly.FromDayNumber(day);
            }
    }

    private static IEnumerable<DateOnly> Monthly(RecurrenceRule rule, DateOnly from, DateOnly to)
    {
        var dayOfMonth = rule.MonthDay ?? rule.StartDate.Day;
        var start = MonthIndex(rule.StartDate);
        for (var month = start + CeilToMultiple(MonthIndex(from) - start, rule.Interval);
             month <= MonthIndex(to);
             month += rule.Interval)
        {
            var date = Clamped(month / 12, month % 12 + 1, dayOfMonth);
            if (date >= from && date <= to) yield return date;
        }
    }

    private static IEnumerable<DateOnly> Yearly(RecurrenceRule rule, DateOnly from, DateOnly to)
    {
        var start = rule.StartDate.Year;
        for (var year = start + CeilToMultiple(from.Year - start, rule.Interval); year <= to.Year; year += rule.Interval)
        {
            var date = Clamped(year, rule.StartDate.Month, rule.StartDate.Day);
            if (date >= from && date <= to) yield return date;
        }
    }

    private static DateOnly Clamped(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    private static int WeekStart(DateOnly date) => date.DayNumber - RecurrenceRule.IsoDayIndex(date.DayOfWeek);

    private static int MonthIndex(DateOnly date) => date.Year * 12 + date.Month - 1;

    private static int CeilToMultiple(int value, int step) => value <= 0 ? 0 : (value + step - 1) / step * step;
}
