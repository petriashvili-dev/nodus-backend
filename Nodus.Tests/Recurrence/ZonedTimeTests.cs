using Nodus.Api.Recurrence;

namespace Nodus.Tests.Recurrence;

public class ZonedTimeTests
{
    private static readonly TimeZoneInfo Berlin = ZonedTime.FindZone("Europe/Berlin");

    [Fact]
    public void Regular_local_time_converts_with_current_offset()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T07:00:00Z"), ZonedTime.ToInstant(new(2026, 7, 1), new(9, 0), Berlin));
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T08:00:00Z"), ZonedTime.ToInstant(new(2026, 1, 15), new(9, 0), Berlin));
    }

    [Fact]
    public void Time_in_spring_gap_is_shifted_forward_by_gap_length()
    {
        // 2026-03-29 02:00 → 03:00 in Berlin; 02:30 does not exist and becomes 03:30 CEST.
        var instant = ZonedTime.ToInstant(new(2026, 3, 29), new(2, 30), Berlin);

        Assert.Equal(DateTimeOffset.Parse("2026-03-29T01:30:00Z"), instant);
        Assert.Equal(new TimeSpan(3, 30, 0), TimeZoneInfo.ConvertTime(instant, Berlin).TimeOfDay);
    }

    [Fact]
    public void Ambiguous_autumn_time_resolves_to_earlier_instant()
    {
        // 2026-10-25 03:00 → 02:00 in Berlin; 02:30 happens twice, the first one is at +02:00.
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z"), ZonedTime.ToInstant(new(2026, 10, 25), new(2, 30), Berlin));
    }

    [Fact]
    public void Same_local_time_keeps_wall_clock_across_dst()
    {
        var before = ZonedTime.ToInstant(new(2026, 3, 28), new(9, 0), Berlin);
        var after = ZonedTime.ToInstant(new(2026, 3, 29), new(9, 0), Berlin);

        Assert.Equal(TimeSpan.FromHours(23), after - before);
    }

    [Theory]
    [InlineData("Europe/Moscow", true)]
    [InlineData("America/New_York", true)]
    [InlineData("Mars/Olympus", false)]
    [InlineData("", false)]
    public void Only_known_iana_zones_are_accepted(string id, bool expected) =>
        Assert.Equal(expected, ZonedTime.TryFindZone(id, out _));
}
