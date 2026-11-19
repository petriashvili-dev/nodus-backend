using System.Globalization;
using System.Text.Json;

namespace Nodus.Api.Common;

/// <summary>
/// Lenient readers for JSON written by others (model output, import files): a missing, null or mistyped value is
/// simply absent instead of an exception. <c>JsonElement.TryGetInt32</c> and friends throw on the wrong value kind,
/// so every accessor checks <see cref="JsonElement.ValueKind"/> first.
/// </summary>
public static class TolerantJson
{
    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss"];

    public static string? StringOrNull(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int? IntOrNull(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out var i)
            ? i
            : null;

    public static bool? BoolOrNull(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public static JsonElement? ObjectOrNull(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>String items of an array property; other items and non-arrays are ignored.</summary>
    public static IEnumerable<string> Strings(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];

    public static DateOnly? ParseDate(string? raw) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Minute precision; accepts <c>HH:mm</c>, <c>H:mm</c> and <c>HH:mm:ss</c>.</summary>
    public static TimeOnly? ParseTime(string? raw) =>
        TimeOnly.TryParseExact(raw, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? new TimeOnly(t.Hour, t.Minute)
            : null;
}
