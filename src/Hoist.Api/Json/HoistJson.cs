using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hoist.Api.Json
{
    /// <summary>
    /// JSON conventions of hoist-core's JSONSerializer (Jackson): dates as epoch millis, LocalDate as
    /// yyyy-MM-dd, NaN/Infinity as null, nulls written, map keys untouched.
    /// </summary>
    public static class HoistJson
    {
        public static readonly JsonSerializerOptions Options = CreateOptions();

        public static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = null,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new EpochMillisDateTimeConverter());
            options.Converters.Add(new EpochMillisDateTimeOffsetConverter());
            options.Converters.Add(new LocalDateConverter());
            options.Converters.Add(new SafeDoubleConverter());
            return options;
        }

        public static string Serialize(object value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);

        public static long ToEpochMillis(DateTime value)
        {
            var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
        }
    }

    /// <summary>DateTime values are UTC within Hoist - Unspecified kinds are treated as UTC.</summary>
    public class EpochMillisDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number) return DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64()).UtcDateTime;
            return DateTime.Parse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(HoistJson.ToEpochMillis(value));
        }
    }

    public class EpochMillisDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number) return DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64());
            return DateTimeOffset.Parse(reader.GetString(), CultureInfo.InvariantCulture);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>LocalDate: written as yyyy-MM-dd; reads yyyy-MM-dd or yyyyMMdd (both accepted by hoist-core).</summary>
    public class LocalDateConverter : JsonConverter<DateOnly>
    {
        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var s = reader.GetString();
            return DateOnly.ParseExact(s, s.Length == 8 ? "yyyyMMdd" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    public class SafeDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType == JsonTokenType.String ? double.Parse(reader.GetString(), CultureInfo.InvariantCulture) : reader.GetDouble();
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
            else writer.WriteNumberValue(value);
        }
    }
}
