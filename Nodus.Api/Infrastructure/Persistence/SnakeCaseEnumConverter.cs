using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Nodus.Api.Infrastructure.Persistence;

/// <summary>Persist enum keys with the same snake_case spelling as the API. EF handles nullable properties.</summary>
public sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => EnumStorageKey.Format(value),
    value => EnumStorageKey.Parse<TEnum>(value)) where TEnum : struct, Enum;

public static class EnumStorageKey
{
    public static string Format<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static TEnum Parse<TEnum>(string value) where TEnum : struct, Enum =>
        Values<TEnum>.ByKey.TryGetValue(value, out var result)
            ? result
            : throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} storage key.");

    private static class Values<TEnum> where TEnum : struct, Enum
    {
        internal static readonly IReadOnlyDictionary<string, TEnum> ByKey = Enum.GetValues<TEnum>()
            .ToDictionary(value => Format(value), value => value, StringComparer.Ordinal);
    }
}
