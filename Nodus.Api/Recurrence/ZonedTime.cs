using System.Collections.Concurrent;

namespace Nodus.Api.Recurrence;

/// <summary>
/// Conversions between node-local calendar values and UTC instants.
/// DST policy: a local time skipped by a forward transition is shifted forward by the length of the gap
/// (02:30 in a 02:00→03:00 gap becomes 03:30); an ambiguous local time resolves to the earlier instant.
/// </summary>
public static class ZonedTime
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Cache = new(StringComparer.Ordinal);

    /// <summary>Finds an IANA time zone; Windows ids and unknown names are rejected.</summary>
    public static bool TryFindZone(string? id, out TimeZoneInfo zone)
    {
        zone = null!;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        var found = Cache.GetOrAdd(id, static key =>
            TimeZoneInfo.TryFindSystemTimeZoneById(key, out var tz) && tz.HasIanaId ? tz : null);
        if (found is null) return false;
        zone = found;
        return true;
    }

    public static TimeZoneInfo FindZone(string id) =>
        TryFindZone(id, out var zone) ? zone : throw new InvalidOperationException($"Unknown IANA time zone '{id}'.");

    public static DateTimeOffset ToInstant(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Interpreting the wall time with the offset in force before the transition shifts it forward by the gap.
            var before = zone.GetUtcOffset(local.AddHours(-6));
            return new DateTimeOffset(DateTime.SpecifyKind(local - before, DateTimeKind.Utc));
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone) => ToInstant(date, TimeOnly.MinValue, zone);

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
