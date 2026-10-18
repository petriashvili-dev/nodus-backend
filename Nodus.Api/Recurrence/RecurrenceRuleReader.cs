using System.Text.Json;
using Nodus.Api.Common;

namespace Nodus.Api.Recurrence;

/// <summary>
/// Reads a recurrence rule from lenient JSON (model output, import files) with defaults: interval 1, start date
/// <c>today</c>, no times (all-day), no week days. Unknown week days and unparseable times are dropped.
/// The result is neither normalised nor validated — callers decide how to report problems.
/// </summary>
public static class RecurrenceRuleReader
{
    /// <param name="invalidDate">Called with the field name when a date is present but unparseable.</param>
    public static RecurrenceRule? Read(JsonElement rule, DateOnly today, Action<string>? invalidDate = null)
    {
        RecurrenceFrequency? frequency = rule.StringOrNull("frequency") switch
        {
            "daily" => RecurrenceFrequency.Daily,
            "weekly" => RecurrenceFrequency.Weekly,
            "monthly" => RecurrenceFrequency.Monthly,
            "yearly" => RecurrenceFrequency.Yearly,
            _ => null
        };
        if (frequency is null) return null;

        return new RecurrenceRule
        {
            Frequency = frequency.Value,
            Interval = rule.IntOrNull("interval") is > 0 and var interval ? interval : 1,
            StartDate = Date(rule, "start_date", invalidDate) ?? today,
            Until = Date(rule, "until", invalidDate),
            Times = rule.Strings("times").Select(TolerantJson.ParseTime).OfType<TimeOnly>().ToArray(),
            WeekDays = frequency == RecurrenceFrequency.Weekly
                ? rule.Strings("week_days").Select(d => Enum.TryParse<DayOfWeek>(d, true, out var day) ? day : (DayOfWeek?)null)
                    .OfType<DayOfWeek>().ToArray()
                : [],
            MonthDay = frequency == RecurrenceFrequency.Monthly ? rule.IntOrNull("month_day") : null
        };
    }

    private static DateOnly? Date(JsonElement rule, string name, Action<string>? invalidDate)
    {
        var raw = rule.StringOrNull(name);
        var date = TolerantJson.ParseDate(raw);
        if (raw is not null && date is null) invalidDate?.Invoke(name);
        return date;
    }
}
