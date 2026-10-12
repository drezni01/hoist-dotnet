using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hoist.Core.Utils
{
    /// <summary>JSON helpers mirroring hoist-core JSONParser / JSONSerializer.</summary>
    public static class JsonUtils
    {
        private static readonly JsonSerializerOptions CompactOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>JSONParser.validate: null counts as valid, as does a single complete JSON value.</summary>
        public static bool IsJson(string s)
        {
            if (s == null) return true;
            try
            {
                using var doc = JsonDocument.Parse(s);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>JSONParser.parseObjectOrArray: null/empty gives null; "[" gives an array, else an object.</summary>
        public static JsonNode ParseObjectOrArray(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var node = JsonNode.Parse(s.Trim());
            if (node == null || node is JsonObject || node is JsonArray) return node;
            throw new JsonException("Expected a JSON object or array");
        }

        /// <summary>JSONSerializer.serialize: compact.</summary>
        public static string Serialize(JsonNode node) => node == null ? "null" : node.ToJsonString(CompactOptions);

        /// <summary>
        /// JSONSerializer.serializePretty - Jackson's DefaultPrettyPrinter: objects one field per line
        /// with "key" : value and two-space indent, arrays inline as [ a, b ], empties as { } and [ ].
        /// </summary>
        public static string SerializePretty(JsonNode node)
        {
            var sb = new StringBuilder();
            WritePretty(sb, node, 0);
            return sb.ToString();
        }

        private static void WritePretty(StringBuilder sb, JsonNode node, int level)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj.Count == 0)
                    {
                        sb.Append("{ }");
                        return;
                    }
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in obj)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('\n').Append(' ', (level + 1) * 2);
                        sb.Append(JsonSerializer.Serialize(kv.Key, CompactOptions)).Append(" : ");
                        WritePretty(sb, kv.Value, level + 1);
                    }
                    sb.Append('\n').Append(' ', level * 2).Append('}');
                    return;
                case JsonArray arr:
                    if (arr.Count == 0)
                    {
                        sb.Append("[ ]");
                        return;
                    }
                    sb.Append("[ ");
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        WritePretty(sb, arr[i], level);
                    }
                    sb.Append(" ]");
                    return;
                case null:
                    sb.Append("null");
                    return;
                default:
                    sb.Append(node.ToJsonString(CompactOptions));
                    return;
            }
        }

        /// <summary>Converts a CLR default value (from code or JSON) to a JsonNode.</summary>
        public static JsonNode ToNode(object value)
        {
            return value switch
            {
                null => null,
                JsonNode n => n.DeepClone(),
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonSerializer.SerializeToNode(value, CompactOptions)
            };
        }

        /// <summary>The string form a value is stored with in a text column (Groovy 'value as String').</summary>
        public static string ToStoredString(object value)
        {
            return value switch
            {
                null => null,
                string s => s,
                bool b => b ? "true" : "false",
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                JsonNode n => Serialize(n),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }

        public static string KindName(JsonNode node)
        {
            return node switch
            {
                JsonObject => "LinkedHashMap",
                JsonArray => "ArrayList",
                JsonValue v when v.GetValueKind() == JsonValueKind.String => "String",
                JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => "Boolean",
                JsonValue v when v.GetValueKind() == JsonValueKind.Number => IsIntegral(v) ? "Integer" : "BigDecimal",
                _ => "Object"
            };
        }

        public static bool IsIntegral(JsonValue v) => v.TryGetValue<long>(out _) || (v.TryGetValue<JsonElement>(out var e) && e.TryGetInt64(out _));
    }

    /// <summary>Groovy String conversions used by hoist-core value parsing.</summary>
    public static class GroovyStrings
    {
        /// <summary>String.toBoolean(): true for "true", "y" or "1" (trimmed, case-insensitive).</summary>
        public static bool ToBoolean(string s)
        {
            var t = s?.Trim().ToLowerInvariant();
            return t == "true" || t == "y" || t == "1";
        }

        public static bool IsInteger(string s) => int.TryParse(s?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        public static bool IsLong(string s) => long.TryParse(s?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        public static bool IsDouble(string s) => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        public static int ToInteger(string s) => int.Parse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        public static long ToLong(string s) => long.Parse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        public static double ToDouble(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>Jackson UPPER_SNAKE_CASE strategy, as used for APP_{CODE}_{KEY} env var names.</summary>
        public static string ToUpperSnakeCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                // Jackson SnakeCaseStrategy: no separator inside a run of capitals ("HTTPServer" -> "HTTPSERVER").
                if (char.IsUpper(c) && i > 0 && s[i - 1] != '_' && !char.IsUpper(s[i - 1])) sb.Append('_');
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString().Replace('-', '_');
        }
    }

    /// <summary>Grails default validation message templates (en), used for ValidationException text.</summary>
    public static class GrailsMessages
    {
        public static string Blank(string prop, string cls) => $"Property [{prop}] of class [class {cls}] cannot be blank";
        public static string Null(string prop, string cls) => $"Property [{prop}] of class [class {cls}] cannot be null";
        public static string MaxSize(string prop, string cls, object value, int max) => $"Property [{prop}] of class [class {cls}] with value [{value}] exceeds the maximum size of [{max}]";
        public static string NotInList(string prop, string cls, object value, IEnumerable<string> list) => $"Property [{prop}] of class [class {cls}] with value [{value}] is not contained within the list [[{string.Join(", ", list)}]]";
        public static string NotUnique(string prop, string cls, object value) => $"Property [{prop}] of class [class {cls}] with value [{value}] must be unique";
        public static string CustomValidation(string prop, string cls, object value) => $"Property [{prop}] of class [class {cls}] with value [{value}] does not pass custom validation";
        public static string TypedConfig(string typedClass, string error) => $"Cannot apply value to {typedClass}: {error}";
    }
}
