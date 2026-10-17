using System.Globalization;

namespace Nodus.Api.Occurrences;

/// <summary>
/// Stable occurrence identifier that does not depend on the editable display time:
/// <c>g{generation}-{yyyyMMdd}-{slot}</c> for series and <c>once</c> for one-time nodes.
/// The date is the calendar date of the rule, the slot is the position in the rule's list of daily times.
/// </summary>
public static class OccurrenceKey
{
    public const string Once = "once";

    public static string For(int generation, DateOnly date, int slot) =>
        string.Create(CultureInfo.InvariantCulture, $"g{generation}-{date:yyyyMMdd}-{slot}");

    public static bool TryParse(string? key, out int generation, out DateOnly date, out int slot)
    {
        generation = slot = 0;
        date = default;
        if (key is null || key.Length > 32 || !key.StartsWith('g')) return false;
        var parts = key[1..].Split('-');
        return parts.Length == 3 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out generation) &&
               DateOnly.TryParseExact(parts[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
               int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out slot);
    }
}
