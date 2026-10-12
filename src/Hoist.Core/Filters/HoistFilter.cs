using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hoist.Core.Exceptions;

namespace Hoist.Core.Filters
{
    /// <summary>
    /// Port of hoist-core data/filter (Filter.parse, FieldFilter, CompoundFilter) for database queries.
    /// getCriterion() becomes a LINQ expression that EF translates to SQL. Case-insensitive matching uses
    /// ToLower(), which EF translates for both MySQL and SQLite.
    /// </summary>
    public abstract class HoistFilter
    {
        /// <summary>Filter.parse: null/empty gives null; an array is an AND compound; {field,...} or {filters,...}.</summary>
        public static HoistFilter Parse(JsonNode spec)
        {
            switch (spec)
            {
                case null:
                    return null;
                case JsonArray arr:
                    return arr.Count == 0 ? null : MakeCompound(arr, "AND");
                case JsonObject obj:
                    if (obj.Count == 0) return null;
                    if (IsTruthy(obj["field"]))
                    {
                        return new FieldFilter(obj["field"].ToString(), obj["op"]?.ToString(), obj["value"]?.DeepClone());
                    }
                    if (obj["filters"] is JsonArray filters && filters.Count > 0)
                    {
                        return MakeCompound(filters, obj["op"]?.ToString());
                    }
                    if (obj["filters"] is JsonArray) return null;
                    throw new HoistException($"Unable to identify filter type: {obj.ToJsonString()}");
                default:
                    throw new HoistException($"Unable to identify filter type: {spec.ToJsonString()}");
            }
        }

        private static HoistFilter MakeCompound(JsonArray filters, string op)
        {
            var ret = new CompoundFilter(filters.Select(Parse).Where(f => f != null).ToList(), op);
            return ret.Filters.Count switch
            {
                0 => null,
                1 => ret.Filters[0],
                _ => ret
            };
        }

        public Expression<Func<T, bool>> ToExpression<T>()
        {
            var param = Expression.Parameter(typeof(T), "x");
            return Expression.Lambda<Func<T, bool>>(Build(param), param);
        }

        public abstract Expression Build(ParameterExpression param);

        protected static bool IsTruthy(JsonNode n) => n != null && !(n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0);
    }

    public class CompoundFilter : HoistFilter
    {
        public CompoundFilter(List<HoistFilter> filters, string op)
        {
            Filters = filters;
            Op = string.IsNullOrEmpty(op) ? "AND" : op.ToUpperInvariant();
            if (Op != "AND" && Op != "OR") throw new HoistException($"CompoundFilter requires 'op' value of 'AND' or 'OR'. Operator '{op}' not recognized.");
        }

        public List<HoistFilter> Filters { get; }
        public string Op { get; }

        public override Expression Build(ParameterExpression param)
        {
            var parts = Filters.Select(f => f.Build(param)).ToList();
            return parts.Aggregate((a, b) => Op == "AND" ? Expression.AndAlso(a, b) : Expression.OrElse(a, b));
        }
    }

    public class FieldFilter : HoistFilter
    {
        public static readonly string[] Operators = { "=", "!=", ">", ">=", "<", "<=", "like", "not like", "begins", "not begins", "ends", "not ends", "includes", "excludes" };
        public static readonly string[] MultiValOperators = { "=", "!=", "like", "not like", "begins", "not begins", "ends", "not ends", "includes", "excludes" };

        private static readonly MethodInfo toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes);
        private static readonly MethodInfo contains = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) });
        private static readonly MethodInfo startsWith = typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) });
        private static readonly MethodInfo endsWith = typeof(string).GetMethod(nameof(string.EndsWith), new[] { typeof(string) });

        public FieldFilter(string field, string op, JsonNode value)
        {
            if (string.IsNullOrEmpty(field)) throw new HoistException("FieldFilter requires a field");
            if (!Operators.Contains(op)) throw new HoistException($"FieldFilter requires valid 'op' value. Operator '{op}' not recognized.");
            if (!MultiValOperators.Contains(op) && value is JsonArray) throw new HoistException($"Operator '{op}' does not support multiple values. Use a CompoundFilter instead.");
            Field = field;
            Op = op;
            Value = value;
        }

        public string Field { get; }
        public string Op { get; }
        public JsonNode Value { get; }

        public override Expression Build(ParameterExpression param)
        {
            var prop = param.Type.GetProperty(Field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? throw new HoistException($"Unknown filter field: {Field}");
            var member = Expression.Property(param, prop);
            var vals = (Value is JsonArray arr ? arr.ToList() : new List<JsonNode> { Value }).Distinct(JsonNodeComparer.Instance).ToList();

            switch (Op)
            {
                case "=":
                {
                    var nonNull = vals.Where(v => v != null).Select(v => Equal(member, v)).ToList();
                    Expression ret = nonNull.Count > 0 ? nonNull.Aggregate(Expression.OrElse) : Expression.Constant(false);
                    return vals.Any(v => v == null) ? Expression.OrElse(IsNull(member), ret) : ret;
                }
                case "!=":
                {
                    // SQL '<>' never matches NULL - EF's C# null semantics would, so exclude NULLs explicitly.
                    var nonNull = vals.Where(v => v != null).Select(v => (Expression)Expression.AndAlso(Expression.Not(IsNull(member)), Expression.Not(Equal(member, v)))).ToList();
                    Expression ret = nonNull.Count > 0 ? nonNull.Aggregate(Expression.AndAlso) : Expression.Constant(true);
                    return vals.Any(v => v == null) ? Expression.AndAlso(Expression.Not(IsNull(member)), ret) : ret;
                }
                case ">": return Expression.GreaterThan(Nullable(member), Constant(Value, member.Type, nullable: true));
                case ">=": return Expression.GreaterThanOrEqual(Nullable(member), Constant(Value, member.Type, nullable: true));
                case "<": return Expression.LessThan(Nullable(member), Constant(Value, member.Type, nullable: true));
                case "<=": return Expression.LessThanOrEqual(Nullable(member), Constant(Value, member.Type, nullable: true));
                case "like": return vals.Select(v => Like(member, v, contains)).Aggregate(Expression.OrElse);
                case "not like": return vals.Select(v => (Expression)Expression.Not(Like(member, v, contains))).Aggregate(Expression.AndAlso);
                case "begins": return vals.Select(v => Like(member, v, startsWith)).Aggregate(Expression.OrElse);
                case "not begins": return vals.Select(v => (Expression)Expression.Not(Like(member, v, startsWith))).Aggregate(Expression.AndAlso);
                case "ends": return vals.Select(v => Like(member, v, endsWith)).Aggregate(Expression.OrElse);
                case "not ends": return vals.Select(v => (Expression)Expression.Not(Like(member, v, endsWith))).Aggregate(Expression.AndAlso);
                default: throw new HoistException("Unsupported operator for Criteria Filter");
            }
        }

        /// <summary>ilike: SQL semantics - a NULL column never matches, so 'not like' excludes NULLs too.</summary>
        private static Expression Like(MemberExpression member, JsonNode value, MethodInfo method)
        {
            var asString = member.Type == typeof(string) ? (Expression)member : Expression.Call(member, member.Type.GetMethod("ToString", Type.EmptyTypes));
            var lowered = Expression.Call(asString, toLower);
            var pattern = Expression.Constant((value?.ToString() ?? "").ToLowerInvariant());
            var call = Expression.Call(lowered, method, pattern);
            return member.Type == typeof(string) ? Expression.AndAlso(Expression.NotEqual(member, Expression.Constant(null, typeof(string))), call) : call;
        }

        private static Expression Equal(MemberExpression member, JsonNode value) => Expression.Equal(Nullable(member), Constant(value, member.Type, nullable: true));

        private static Expression IsNull(MemberExpression member)
        {
            if (member.Type.IsValueType && System.Nullable.GetUnderlyingType(member.Type) == null) return Expression.Constant(false);
            return Expression.Equal(member, Expression.Constant(null, member.Type));
        }

        private static Expression Nullable(MemberExpression member)
        {
            if (!member.Type.IsValueType || System.Nullable.GetUnderlyingType(member.Type) != null) return member;
            return Expression.Convert(member, typeof(Nullable<>).MakeGenericType(member.Type));
        }

        private static Expression Constant(JsonNode value, Type memberType, bool nullable)
        {
            var underlying = System.Nullable.GetUnderlyingType(memberType) ?? memberType;
            var targetType = underlying.IsValueType && nullable ? typeof(Nullable<>).MakeGenericType(underlying) : memberType;
            if (value == null) return Expression.Constant(null, targetType);
            return Expression.Constant(Convert(value, underlying), targetType);
        }

        /// <summary>Converts a filter value to the property type. Dates accept epoch ms or ISO strings.</summary>
        private static object Convert(JsonNode value, Type type)
        {
            var element = JsonSerializer.SerializeToElement(value);
            if (type == typeof(string)) return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            if (type == typeof(DateTime))
            {
                return element.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(element.GetInt64()).UtcDateTime
                    : DateTime.Parse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            }
            if (type == typeof(int)) return element.ValueKind == JsonValueKind.Number ? (int)element.GetDouble() : int.Parse(element.GetString(), CultureInfo.InvariantCulture);
            if (type == typeof(long)) return element.ValueKind == JsonValueKind.Number ? (long)element.GetDouble() : long.Parse(element.GetString(), CultureInfo.InvariantCulture);
            if (type == typeof(double)) return element.ValueKind == JsonValueKind.Number ? element.GetDouble() : double.Parse(element.GetString(), CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : bool.Parse(element.GetString());
            throw new HoistException($"Unsupported filter field type: {type.Name}");
        }

        private class JsonNodeComparer : IEqualityComparer<JsonNode>
        {
            public static readonly JsonNodeComparer Instance = new JsonNodeComparer();
            public bool Equals(JsonNode x, JsonNode y) => JsonNode.DeepEquals(x, y);
            public int GetHashCode(JsonNode obj) => obj?.ToJsonString().GetHashCode() ?? 0;
        }
    }
}
