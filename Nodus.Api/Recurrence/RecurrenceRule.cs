namespace Nodus.Api.Recurrence;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly
}

/// <summary>
/// Strictly defined recurrence schema (a deliberate subset, not RFC 5545 RRULE).
/// All dates and times are local to the node timezone.
/// </summary>
public sealed record RecurrenceRule
{
    public const int MaxInterval = 366;
    public const int MaxTimesPerDay = 12;

    public RecurrenceFrequency Frequency { get; init; }

    /// <summary>Every N days/weeks/months/years, counted from <see cref="StartDate"/>.</summary>
    public int Interval { get; init; } = 1;

    public DateOnly StartDate { get; init; }

    /// <summary>Inclusive last date.</summary>
    public DateOnly? Until { get; init; }

    /// <summary>
    /// Local times within a day, ascending; empty means a single all-day occurrence.
    /// The position in this list is the occurrence slot used in the occurrence key.
    /// </summary>
    public IReadOnlyList<TimeOnly> Times { get; init; } = [];

    /// <summary>Weekly only; empty means the weekday of <see cref="StartDate"/>.</summary>
    public IReadOnlyList<DayOfWeek> WeekDays { get; init; } = [];

    /// <summary>Monthly only; null means the day of <see cref="StartDate"/>. Clamped to the month length.</summary>
    public int? MonthDay { get; init; }

    /// <summary>Drops seconds, sorts and de-duplicates lists so that equal rules compare equal.</summary>
    public RecurrenceRule Normalize() => this with
    {
        Times = Times.Select(t => new TimeOnly(t.Hour, t.Minute)).Distinct().Order().ToArray(),
        WeekDays = Frequency == RecurrenceFrequency.Weekly
            ? WeekDays.Distinct().OrderBy(IsoDayIndex).ToArray()
            : [],
        MonthDay = Frequency == RecurrenceFrequency.Monthly ? MonthDay : null
    };

    public IEnumerable<string> Validate()
    {
        if (!Enum.IsDefined(Frequency)) yield return "Unknown frequency.";
        if (Interval < 1 || Interval > MaxInterval) yield return $"Interval must be between 1 and {MaxInterval}.";
        if (Until is { } until && until < StartDate) yield return "Until must not be earlier than start date.";
        if (Times.Count > MaxTimesPerDay) yield return $"At most {MaxTimesPerDay} times per day are allowed.";
        if (Frequency != RecurrenceFrequency.Weekly && WeekDays.Count > 0)
            yield return "Week days are allowed for weekly recurrence only.";
        if (WeekDays.Any(d => !Enum.IsDefined(d))) yield return "Unknown week day.";
        if (MonthDay is not null && Frequency != RecurrenceFrequency.Monthly)
            yield return "Month day is allowed for monthly recurrence only.";
        if (MonthDay is < 1 or > 31) yield return "Month day must be between 1 and 31.";
    }

    public bool Equals(RecurrenceRule? other) =>
        other is not null && Frequency == other.Frequency && Interval == other.Interval &&
        StartDate == other.StartDate && Until == other.Until && MonthDay == other.MonthDay &&
        Times.SequenceEqual(other.Times) && WeekDays.SequenceEqual(other.WeekDays);

    public override int GetHashCode() => HashCode.Combine(Frequency, Interval, StartDate, Until, MonthDay, Times.Count);

    /// <summary>Monday = 0 … Sunday = 6.</summary>
    internal static int IsoDayIndex(DayOfWeek day) => ((int)day + 6) % 7;
}
