using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hoist.Core.Utils;

namespace Hoist.Core.Configuration
{
    /// <summary>
    /// Data-driven port of hoist-core TypedConfigMap. Each typed class is a list of declared fields
    /// with a type and default (BuiltIn/hoist-builtins.json). Resolve() binds a stored map onto the
    /// defaults: unknown keys dropped, strict type checks (numbers may widen), nested typed objects
    /// and lists of them built recursively, output in declaration order - exactly formatForJSON().
    /// </summary>
    public class TypedConfigRegistry
    {
        private static readonly Lazy<TypedConfigRegistry> builtIn = new Lazy<TypedConfigRegistry>(() =>
            FromJson(BuiltInSpecs.Document["typedClasses"].AsObject()));

        private readonly Dictionary<string, TypedClassDef> classes;

        public TypedConfigRegistry(Dictionary<string, TypedClassDef> classes)
        {
            this.classes = classes;
        }

        public static TypedConfigRegistry BuiltIn => builtIn.Value;

        public bool Has(string className) => className != null && classes.ContainsKey(className);

        public static TypedConfigRegistry FromJson(JsonObject typedClasses)
        {
            var defs = new Dictionary<string, TypedClassDef>();
            foreach (var kv in typedClasses)
            {
                var fields = kv.Value["fields"].AsArray().Select(f => new TypedFieldDef
                {
                    Name = f["name"].GetValue<string>(),
                    Aliases = f["aliases"]?.AsArray().Select(a => a.GetValue<string>()).ToArray() ?? new string[0],
                    Type = f["type"].GetValue<string>(),
                    Class = f["class"]?.GetValue<string>(),
                    ItemClass = f["itemClass"]?.GetValue<string>(),
                    Default = f["default"]?.DeepClone()
                }).ToList();
                defs[kv.Key] = new TypedClassDef(kv.Key, fields);
            }
            return new TypedConfigRegistry(defs);
        }

        /// <summary>Equivalent of new TypedClass(map).formatForJSON(). Throws ArgumentException on a type mismatch.</summary>
        public JsonObject Resolve(string className, JsonObject args)
        {
            var def = classes[className];
            var values = new Dictionary<string, JsonNode>();
            foreach (var field in def.Fields) values[field.Name] = DefaultFor(field);

            if (args != null)
            {
                foreach (var kv in args)
                {
                    var field = def.Find(kv.Key);
                    if (field == null) continue; // hoist-core logs a (deduplicated) warning and ignores unknown keys
                    values[field.Name] = Bind(def, field, kv.Value, values[field.Name]);
                }
            }

            var ret = new JsonObject();
            foreach (var field in def.Fields) ret[field.Name] = values[field.Name]?.DeepClone();
            return ret;
        }

        private JsonNode DefaultFor(TypedFieldDef field)
        {
            if (field.Type == "object" && field.Class != null)
            {
                return Resolve(field.Class, field.Default as JsonObject ?? new JsonObject());
            }
            if (field.Type == "list" && field.ItemClass != null && field.Default is JsonArray items)
            {
                return new JsonArray(items.Select(i => (JsonNode)Resolve(field.ItemClass, i as JsonObject ?? new JsonObject())).ToArray());
            }
            return field.Default?.DeepClone();
        }

        private JsonNode Bind(TypedClassDef def, TypedFieldDef field, JsonNode value, JsonNode current)
        {
            if (value == null) return null;

            if (field.Type == "object" && field.Class != null && value is JsonObject obj)
            {
                // Existing (default) instance is re-initialised with the new values: merge onto it.
                var merged = current as JsonObject ?? new JsonObject();
                var combined = (JsonObject)merged.DeepClone();
                foreach (var kv in obj) combined[kv.Key] = kv.Value?.DeepClone();
                return Resolve(field.Class, combined);
            }
            if (field.Type == "list" && value is JsonArray arr)
            {
                if (field.ItemClass == null) return arr.DeepClone();
                return new JsonArray(arr.Select(i => i is JsonObject io ? Resolve(field.ItemClass, io) : i?.DeepClone()).ToArray());
            }
            if (field.Type == "map" && value is JsonObject map) return map.DeepClone();

            CheckAssignable(def.Name, field, value);
            return value.DeepClone();
        }

        private static void CheckAssignable(string className, TypedFieldDef field, JsonNode value)
        {
            var kind = value.GetValueKind();
            var ok = field.Type switch
            {
                "any" => true,
                "bool" => kind is JsonValueKind.True or JsonValueKind.False,
                "int" or "long" or "double" => kind == JsonValueKind.Number,
                "string" => kind == JsonValueKind.String,
                "list" => kind == JsonValueKind.Array,
                "map" or "object" => kind == JsonValueKind.Object,
                _ => true
            };
            if (!ok)
            {
                throw new ArgumentException($"Field '{field.Name}' on {className} expects {JavaTypeName(field)} but got {JsonUtils.KindName(value)}");
            }
        }

        private static string JavaTypeName(TypedFieldDef field) => field.Type switch
        {
            "bool" => "Boolean",
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "string" => "String",
            "list" => "List",
            "map" => "Map",
            "object" => field.Class,
            _ => "Object"
        };
    }

    public class TypedClassDef
    {
        public TypedClassDef(string name, List<TypedFieldDef> fields)
        {
            Name = name;
            Fields = fields;
        }

        public string Name { get; }
        public List<TypedFieldDef> Fields { get; }

        public TypedFieldDef Find(string key) => Fields.FirstOrDefault(f => f.Name == key) ?? Fields.FirstOrDefault(f => f.Aliases.Contains(key));
    }

    public class TypedFieldDef
    {
        public string Name { get; set; }
        public string[] Aliases { get; set; }

        /// <summary>bool | int | long | double | string | list | map | object | any</summary>
        public string Type { get; set; }
        public string Class { get; set; }
        public string ItemClass { get; set; }
        public JsonNode Default { get; set; }
    }

    /// <summary>hoist-core ConfigSpec.</summary>
    public class ConfigSpec
    {
        public string Name { get; set; }
        public string ValueType { get; set; }
        public object DefaultValue { get; set; }
        public bool ClientVisible { get; set; }
        public string GroupName { get; set; } = "Default";
        public string Note { get; set; }

        /// <summary>Name of a typed class registered in the TypedConfigRegistry.</summary>
        public string TypedClass { get; set; }
    }

    /// <summary>hoist-core PreferenceSpec.</summary>
    public class PreferenceSpec
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public object DefaultValue { get; set; }
        public string GroupName { get; set; } = "Default";
        public string Notes { get; set; }
    }

    /// <summary>The built-in configs and prefs hoist-core's BootStrap ensures exist.</summary>
    public static class BuiltInSpecs
    {
        private static readonly Lazy<JsonObject> document = new Lazy<JsonObject>(() =>
        {
            var asm = Assembly.GetExecutingAssembly();
            var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith("hoist-builtins.json"));
            using var stream = asm.GetManifestResourceStream(resource);
            return JsonNode.Parse(stream).AsObject();
        });

        public static JsonObject Document => document.Value;

        public static IReadOnlyList<ConfigSpec> Configs => Document["configs"].AsArray().Select(c => new ConfigSpec
        {
            Name = c["name"].GetValue<string>(),
            ValueType = c["valueType"].GetValue<string>(),
            DefaultValue = c["defaultValue"]?.DeepClone(),
            ClientVisible = c["clientVisible"]?.GetValue<bool>() ?? false,
            GroupName = c["groupName"]?.GetValue<string>() ?? "Default",
            Note = c["note"]?.GetValue<string>(),
            TypedClass = c["typedClass"]?.GetValue<string>()
        }).ToList();

        public static IReadOnlyList<PreferenceSpec> Prefs => Document["prefs"].AsArray().Select(p => new PreferenceSpec
        {
            Name = p["name"].GetValue<string>(),
            Type = p["type"].GetValue<string>(),
            DefaultValue = p["defaultValue"]?.DeepClone(),
            GroupName = p["groupName"]?.GetValue<string>() ?? "Default",
            Notes = p["notes"]?.GetValue<string>()
        }).ToList();
    }
}
