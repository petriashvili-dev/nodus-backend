using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nodus.Api.Common;

/// <summary>Single JSON contract for HTTP, persisted payloads and stored idempotent responses.</summary>
public static class NodusJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.Converters.Add(new MinuteTimeConverter());
        return options;
    }

    /// <summary>Local times are minute-precise: written as <c>HH:mm</c>, read from <c>HH:mm</c> or <c>HH:mm:ss</c>.</summary>
    private sealed class MinuteTimeConverter : JsonConverter<TimeOnly>
    {
        private static readonly string[] Formats = ["HH:mm", "HH:mm:ss"];

        public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (!TimeOnly.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new JsonException("Time must be in HH:mm format.");
            return new TimeOnly(time.Hour, time.Minute);
        }

        public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("HH:mm", CultureInfo.InvariantCulture));
    }
}
