using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hoist.Core.Configuration;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hoist.Core.Services
{
    /// <summary>Options for AppConfig.externalValue / parseValue.</summary>
    public class ConfigValueOptions
    {
        public bool JsonAsObject { get; set; }
        public bool ObscurePassword { get; set; }
        public bool DigestPassword { get; set; }
        public bool DecryptPassword { get; set; }
    }

    /// <summary>
    /// Port of hoist-core config/ConfigService plus the value logic of config/AppConfig
    /// (externalValue, parseValue, isValid, pwd encryption). Values are returned as JsonNode, the
    /// .NET stand-in for Groovy's untyped Object (string, number, bool, map or list).
    /// </summary>
    public class ConfigService
    {
        private static readonly Regex groupPattern = new Regex(@"\[([\w-]+)\]");

        private readonly IConfigRepository repo;
        private readonly IInstanceConfig instanceConfig;
        private readonly IConfigEncryptor encryptor;
        private readonly IIdentityAccessor identity;
        private readonly ILogger<ConfigService> logger;
        private readonly TypedConfigRegistry typedRegistry;

        // Registry of config name -> typed class name, populated by EnsureRequiredConfigsCreatedAsync.
        private readonly Dictionary<string, string> configTypeByName = new Dictionary<string, string>();

        public ConfigService(
            IConfigRepository repo,
            IInstanceConfig instanceConfig,
            IConfigEncryptor encryptor,
            IIdentityAccessor identity,
            TypedConfigRegistry typedRegistry,
            ILogger<ConfigService> logger)
        {
            this.repo = repo;
            this.instanceConfig = instanceConfig;
            this.encryptor = encryptor;
            this.identity = identity;
            this.typedRegistry = typedRegistry;
            this.logger = logger;
        }

        //------------------------
        // Typed getters
        //------------------------
        public async Task<string> GetStringAsync(string name, string notFoundValue = null) => (await GetInternalAsync(name, "string", notFoundValue))?.GetValue<string>();
        public async Task<string> GetPwdAsync(string name, string notFoundValue = null) => (await GetInternalAsync(name, "pwd", notFoundValue))?.GetValue<string>();
        public async Task<int?> GetIntAsync(string name, int? notFoundValue = null) => (await GetInternalAsync(name, "int", notFoundValue))?.GetValue<int>();
        public async Task<long?> GetLongAsync(string name, long? notFoundValue = null) => (await GetInternalAsync(name, "long", notFoundValue))?.GetValue<long>();
        public async Task<double?> GetDoubleAsync(string name, double? notFoundValue = null) => (await GetInternalAsync(name, "double", notFoundValue))?.GetValue<double>();
        public async Task<bool?> GetBoolAsync(string name, bool? notFoundValue = null) => (await GetInternalAsync(name, "bool", notFoundValue))?.GetValue<bool>();
        public async Task<JsonObject> GetMapAsync(string name, JsonObject notFoundValue = null) => (JsonObject)await GetInternalAsync(name, "json", notFoundValue);
        public async Task<JsonArray> GetListAsync(string name, JsonArray notFoundValue = null) => (JsonArray)await GetInternalAsync(name, "json", notFoundValue);

        public async Task<string> GetStringIfSetAsync(string name) => ValueIfSet(await GetStringAsync(name));
        public async Task<string> GetPwdIfSetAsync(string name) => ValueIfSet(await GetPwdAsync(name));

        /// <summary>getObject(Class): the typed config registered for this class, with defaults applied.</summary>
        public async Task<JsonObject> GetObjectAsync(string typedClass)
        {
            var name = configTypeByName.FirstOrDefault(kv => kv.Value == typedClass).Key;
            if (name == null)
            {
                throw new HoistException($"{typedClass} is not registered as a typedClass — declare it via ensureRequiredConfigsCreated to be loadable via getObject()");
            }
            return typedRegistry.Resolve(typedClass, await GetMapAsync(name, new JsonObject()));
        }

        public async Task<bool> HasConfigAsync(string name) => await repo.FindByNameAsync(name) != null;

        /// <summary>Comma-separated list, trimmed; a token like [otherConfig] expands that config recursively.</summary>
        public async Task<List<string>> GetStringListAsync(string configName)
        {
            var raw = await GetStringAsync(configName, "");
            var ret = new List<string>();
            foreach (var token in raw.Split(',').Select(t => t.Trim()))
            {
                var matches = groupPattern.Matches(token);
                if (matches.Count == 1) ret.AddRange(await GetStringListAsync(matches[0].Groups[1].Value));
                else ret.Add(token);
            }
            return ret;
        }

        /// <summary>All clientVisible configs, keyed by name - typed configs rendered with their defaults.</summary>
        public async Task<Dictionary<string, JsonNode>> GetClientConfigAsync()
        {
            var ret = new Dictionary<string, JsonNode>();
            foreach (var config in (await repo.ListAsync()).Where(c => c.ClientVisible))
            {
                try
                {
                    var external = ExternalValue(config, new ConfigValueOptions { ObscurePassword = true, JsonAsObject = true });
                    ret[config.Name] = configTypeByName.TryGetValue(config.Name, out var typedClass) && external is JsonObject map
                        ? typedRegistry.Resolve(typedClass, map)
                        : external;
                }
                catch (Exception e)
                {
                    logger.LogError("Skipping config '{Name}' — could not be prepared for client | {Message}", config.Name, e.Message);
                }
            }
            return ret;
        }

        //------------------------
        // Writes
        //------------------------
        public async Task<AppConfig> SetValueAsync(string name, object value, string lastUpdatedBy = null)
        {
            var config = await repo.FindByNameAsync(name) ?? throw new HoistException($"No config found with name: [{name}]");
            if (config.ValueType == "json" && value is not string) value = JsonUtils.SerializePretty(JsonUtils.ToNode(value));
            config.Value = JsonUtils.ToStoredString(value);
            config.LastUpdatedBy = lastUpdatedBy ?? identity?.AuthUsername ?? "hoist-config-service";
            return await SaveAsync(config, valueChanged: true);
        }

        /// <summary>Validates and saves a config, encrypting pwd values on insert or when the value changed.</summary>
        public async Task<AppConfig> SaveAsync(AppConfig config, bool valueChanged)
        {
            await ValidateAsync(config);
            if (config.ValueType == "pwd" && (valueChanged || config.Id == 0))
            {
                config.Value = encryptor.Encrypt(config.Value);
            }
            return await repo.SaveAsync(config);
        }

        public async Task EnsureRequiredConfigsCreatedAsync(IEnumerable<ConfigSpec> specs)
        {
            var current = await repo.ListAsync();
            var created = 0;
            foreach (var spec in specs)
            {
                var existing = current.FirstOrDefault(c => c.Name == spec.Name);
                var defaultVal = spec.DefaultValue;
                if (spec.TypedClass != null && defaultVal == null) defaultVal = new JsonObject();

                if (existing == null)
                {
                    if (spec.ValueType == "json") defaultVal = JsonUtils.SerializePretty(JsonUtils.ToNode(defaultVal));
                    await SaveAsync(new AppConfig
                    {
                        Name = spec.Name,
                        ValueType = spec.ValueType,
                        Value = JsonUtils.ToStoredString(defaultVal),
                        GroupName = spec.GroupName,
                        ClientVisible = spec.ClientVisible,
                        LastUpdatedBy = "hoist-bootstrap",
                        Note = spec.Note ?? ""
                    }, valueChanged: true);
                    logger.LogWarning("Required config {Name} missing and created with default value | verify default is appropriate for this application", spec.Name);
                    created++;
                }
                else
                {
                    if (existing.ValueType != spec.ValueType)
                    {
                        logger.LogError("Unexpected value type for required config {Name} | expected {Expected} got {Actual} | review and fix!", spec.Name, spec.ValueType, existing.ValueType);
                    }
                    if (existing.ClientVisible != spec.ClientVisible)
                    {
                        logger.LogError("Unexpected clientVisible for required config {Name} | expected {Expected} got {Actual} | review and fix!", spec.Name, spec.ClientVisible, existing.ClientVisible);
                    }
                }

                if (spec.TypedClass != null)
                {
                    if (!typedRegistry.Has(spec.TypedClass)) throw new HoistException($"typedClass for config '{spec.Name}' is not a registered typed config — got {spec.TypedClass}");
                    configTypeByName[spec.Name] = spec.TypedClass;
                }
            }
            logger.LogDebug("Validated presence of {Count} required configs | created {Created}", specs.Count(), created);
        }

        //------------------------
        // AppConfig value logic
        //------------------------
        public string GetTypedClass(string name) => configTypeByName.TryGetValue(name, out var c) ? c : null;

        /// <summary>AppConfig.externalValue: the instance-config override when set and parseable, else the stored value.</summary>
        public JsonNode ExternalValue(AppConfig config, ConfigValueOptions opts)
        {
            var overrideValue = OverrideValue(config, opts);
            return overrideValue ?? ParseValue(config, config.Value, opts, isOverride: false);
        }

        /// <summary>The parsed instance-config override, or null when absent or unparseable.</summary>
        public JsonNode OverrideValue(AppConfig config, ConfigValueOptions opts)
        {
            var raw = instanceConfig.Get(config.Name);
            if (raw == null) return null;
            try
            {
                return ParseValue(config, raw, opts, isOverride: true);
            }
            catch (Exception e)
            {
                logger.LogTrace("InstanceConfig override found for '{Name}' but cannot be parsed - override will be ignored | {Message}", config.Name, e.Message);
                return null;
            }
        }

        public JsonNode ParseValue(AppConfig config, string value, ConfigValueOptions opts, bool isOverride)
        {
            switch (config.ValueType)
            {
                case "json": return opts.JsonAsObject ? JsonUtils.ParseObjectOrArray(value) : JsonValue.Create(value);
                case "int": return JsonValue.Create(GroovyStrings.ToInteger(value));
                case "long": return JsonValue.Create(GroovyStrings.ToLong(value));
                case "double": return JsonValue.Create(GroovyStrings.ToDouble(value));
                case "bool": return JsonValue.Create(GroovyStrings.ToBoolean(value));
                case "pwd":
                    if (opts.ObscurePassword) return JsonValue.Create("*********");
                    // Override values are not encrypted by our encryptor - treat them as plaintext.
                    if (opts.DigestPassword) return JsonValue.Create(encryptor.Digest(isOverride ? value : encryptor.Decrypt(value)));
                    if (opts.DecryptPassword) return JsonValue.Create(isOverride ? value : encryptor.Decrypt(value));
                    return JsonValue.Create(value);
                default: return JsonValue.Create(value);
            }
        }

        /// <summary>Typed-class view of the effective value (admin 'resolvedValue'), or null.</summary>
        public JsonNode GetResolvedConfigValue(AppConfig config)
        {
            if (!configTypeByName.TryGetValue(config.Name, out var typedClass)) return null;
            try
            {
                return ExternalValue(config, new ConfigValueOptions { JsonAsObject = true }) is JsonObject map ? typedRegistry.Resolve(typedClass, map) : null;
            }
            catch (Exception e)
            {
                logger.LogDebug("Could not compute resolved value for config '{Name}' | {Message}", config.Name, e.Message);
                return null;
            }
        }

        /// <summary>Typed-class defaults (admin 'defaultValue'), or null.</summary>
        public JsonNode GetDefaultConfigValue(AppConfig config)
        {
            return configTypeByName.TryGetValue(config.Name, out var typedClass) ? typedRegistry.Resolve(typedClass, new JsonObject()) : null;
        }

        /// <summary>
        /// AppConfig.formatForJSON (admin): value parsed with pwd shown as a digest and json kept as a string,
        /// plus the instance-config override and the typed-config resolved / default values.
        /// </summary>
        public Dictionary<string, object> FormatForAdmin(AppConfig c)
        {
            JsonNode value;
            try
            {
                value = ParseValue(c, c.Value, new ConfigValueOptions { DigestPassword = true }, isOverride: false);
            }
            catch (Exception)
            {
                value = JsonValue.Create(c.Value);
            }
            return new Dictionary<string, object>
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["groupName"] = c.GroupName,
                ["valueType"] = c.ValueType,
                ["value"] = value,
                ["overrideValue"] = OverrideValue(c, new ConfigValueOptions { DigestPassword = true }),
                ["resolvedValue"] = GetResolvedConfigValue(c),
                ["defaultValue"] = GetDefaultConfigValue(c),
                ["clientVisible"] = c.ClientVisible,
                ["note"] = c.Note,
                ["lastUpdatedBy"] = c.LastUpdatedBy,
                ["lastUpdated"] = c.LastUpdated
            };
        }

        /// <summary>The admin digest of a stored pwd value - used to spot an unchanged value posted back by the admin form.</summary>
        public string DigestOfStoredPwd(AppConfig c)
        {
            try
            {
                return encryptor.Digest(encryptor.Decrypt(c.Value));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>GORM constraints + AppConfig.isValid; throws ValidationException with Grails messages.</summary>
        public async Task ValidateAsync(AppConfig c)
        {
            const string cls = "io.xh.hoist.config.AppConfig";
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(c.Name)) errors.Add(c.Name == null ? GrailsMessages.Null("name", cls) : GrailsMessages.Blank("name", cls));
            else if (c.Name.Length > 50) errors.Add(GrailsMessages.MaxSize("name", cls, c.Name, 50));
            else if (await repo.NameExistsAsync(c.Name, c.Id)) errors.Add(GrailsMessages.NotUnique("name", cls, c.Name));

            if (!AppConfig.Types.Contains(c.ValueType)) errors.Add(GrailsMessages.NotInList("valueType", cls, c.ValueType, AppConfig.Types));
            if (c.Note?.Length > 1200) errors.Add(GrailsMessages.MaxSize("note", cls, c.Note, 1200));
            if (c.LastUpdatedBy?.Length > 50) errors.Add(GrailsMessages.MaxSize("lastUpdatedBy", cls, c.LastUpdatedBy, 50));
            if (string.IsNullOrWhiteSpace(c.GroupName)) errors.Add(c.GroupName == null ? GrailsMessages.Null("groupName", cls) : GrailsMessages.Blank("groupName", cls));

            if (string.IsNullOrWhiteSpace(c.Value))
            {
                errors.Add(c.Value == null ? GrailsMessages.Null("value", cls) : GrailsMessages.Blank("value", cls));
            }
            else
            {
                var v = c.Value;
                var invalid = c.ValueType switch
                {
                    "bool" => !(v == "true" || v == "false"),
                    "int" => !GroovyStrings.IsInteger(v),
                    "long" => !GroovyStrings.IsLong(v),
                    "double" => !GroovyStrings.IsDouble(v),
                    "json" => !JsonUtils.IsJson(v),
                    _ => false
                };
                if (invalid)
                {
                    errors.Add(GrailsMessages.CustomValidation("value", cls, v));
                }
                else if (c.ValueType == "json" && configTypeByName.TryGetValue(c.Name ?? "", out var typedClass))
                {
                    try
                    {
                        typedRegistry.Resolve(typedClass, JsonNode.Parse(v) as JsonObject);
                    }
                    catch (Exception e)
                    {
                        errors.Add(GrailsMessages.TypedConfig(typedClass, e.Message));
                    }
                }
            }

            if (errors.Count > 0) throw new ValidationException(errors);
        }

        //------------------------
        // Implementation
        //------------------------
        private static string ValueIfSet(string value) => value == AppConfig.None ? null : value;

        private async Task<JsonNode> GetInternalAsync(string name, string valueType, object notFoundValue)
        {
            var c = await repo.FindByNameAsync(name);
            if (c == null)
            {
                if (notFoundValue != null) return JsonUtils.ToNode(notFoundValue);
                throw new HoistException($"No config found with name: [{name}]");
            }
            if (valueType != c.ValueType)
            {
                throw new HoistException($"Unexpected type for config: [{name}] | config is {c.ValueType} | expected {valueType}");
            }
            return ExternalValue(c, new ConfigValueOptions { DecryptPassword = true, JsonAsObject = true });
        }
    }
}
