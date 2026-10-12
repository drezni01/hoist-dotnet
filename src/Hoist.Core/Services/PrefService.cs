using System.Text.Json.Nodes;
using Hoist.Core.Configuration;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hoist.Core.Services
{
    /// <summary>
    /// Port of hoist-core pref/PrefService, plus the value logic of Preference / UserPreference
    /// (externalDefaultValue, externalUserValue, isValidForType). Reads and writes default to the
    /// current (apparent) user; writes record the authenticated user as lastUpdatedBy.
    /// </summary>
    public class PrefService : IHoistInitializer
    {
        private readonly IPrefRepository repo;
        private readonly IIdentityAccessor identity;
        private readonly ILogger<PrefService> logger;

        public PrefService(IPrefRepository repo, IIdentityAccessor identity, ILogger<PrefService> logger)
        {
            this.repo = repo;
            this.identity = identity;
            this.logger = logger;
        }

        public int Order => 5;

        public Task InitAsync() => EnsureRequiredPrefsCreatedAsync(BuiltInSpecs.Prefs);

        //------------------------
        // Typed reads
        //------------------------
        public async Task<string> GetStringAsync(string key, string username = null) => (await GetUserPreferenceAsync(key, "string", username))?.GetValue<string>();
        public async Task<int> GetIntAsync(string key, string username = null) => (await GetUserPreferenceAsync(key, "int", username)).GetValue<int>();
        public async Task<long> GetLongAsync(string key, string username = null) => (await GetUserPreferenceAsync(key, "long", username)).GetValue<long>();
        public async Task<double> GetDoubleAsync(string key, string username = null) => (await GetUserPreferenceAsync(key, "double", username)).GetValue<double>();
        public async Task<bool> GetBoolAsync(string key, string username = null) => (await GetUserPreferenceAsync(key, "bool", username)).GetValue<bool>();
        public async Task<JsonObject> GetMapAsync(string key, string username = null) => (JsonObject)await GetUserPreferenceAsync(key, "json", username);
        public async Task<JsonArray> GetListAsync(string key, string username = null) => (JsonArray)await GetUserPreferenceAsync(key, "json", username);

        //------------------------
        // Writes
        //------------------------
        public Task SetStringAsync(string key, string value, string username = null) => SetUserPreferenceAsync(key, value, "string", username);
        public Task SetIntAsync(string key, int value, string username = null) => SetUserPreferenceAsync(key, JsonUtils.ToStoredString(value), "int", username);
        public Task SetLongAsync(string key, long value, string username = null) => SetUserPreferenceAsync(key, JsonUtils.ToStoredString(value), "long", username);
        public Task SetDoubleAsync(string key, double value, string username = null) => SetUserPreferenceAsync(key, JsonUtils.ToStoredString(value), "double", username);
        public Task SetBoolAsync(string key, bool value, string username = null) => SetUserPreferenceAsync(key, value ? "true" : "false", "bool", username);
        public Task SetMapAsync(string key, JsonObject value, string username = null) => SetUserPreferenceAsync(key, JsonUtils.Serialize(value), "json", username);
        public Task SetListAsync(string key, JsonArray value, string username = null) => SetUserPreferenceAsync(key, JsonUtils.Serialize(value), "json", username);

        /// <summary>setPreference: stores the string as-is, without a type check (domain validation still applies).</summary>
        public Task SetPreferenceAsync(string key, string value, string username = null) => SetUserPreferenceAsync(key, value, null, username);

        public async Task UnsetPreferenceAsync(string key, string username = null)
        {
            var pref = await repo.FindByNameAsync(key);
            if (pref == null) return;
            var userPref = await repo.FindUserPrefAsync(pref.Id, username ?? identity.Username);
            if (userPref != null) await repo.DeleteUserPrefAsync(userPref);
        }

        public Task ClearPreferencesAsync(string username = null) => repo.DeleteUserPrefsAsync(username ?? identity.Username);

        public async Task<bool> IsUnsetAsync(string key, string username = null)
        {
            var pref = await GetDefaultPreferenceAsync(key, null);
            return await repo.FindUserPrefAsync(pref.Id, username ?? identity.Username) == null;
        }

        //------------------------
        // Client config
        //------------------------
        /// <summary>All prefs for the current user: {name: {type, value, defaultValue, isSet}}.</summary>
        public async Task<Dictionary<string, object>> GetClientConfigAsync()
        {
            var userPrefs = (await repo.ListUserPrefsAsync(identity.Username)).ToDictionary(u => u.PreferenceId);
            var ret = new Dictionary<string, object>();
            foreach (var pref in await repo.ListAsync())
            {
                try
                {
                    ret[pref.Name] = FormatForClient(pref, userPrefs.GetValueOrDefault(pref.Id));
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Exception while getting client preference: '{Name}'", pref.Name);
                }
            }
            return ret;
        }

        /// <summary>Client format for just the given keys (unknown keys are skipped).</summary>
        public async Task<Dictionary<string, object>> GetLimitedClientConfigAsync(IEnumerable<string> keys)
        {
            var wanted = keys.ToHashSet();
            var userPrefs = (await repo.ListUserPrefsAsync(identity.Username)).ToDictionary(u => u.PreferenceId);
            return (await repo.ListAsync())
                .Where(p => wanted.Contains(p.Name))
                .ToDictionary(p => p.Name, p => (object)FormatForClient(p, userPrefs.GetValueOrDefault(p.Id)));
        }

        public async Task EnsureRequiredPrefsCreatedAsync(IEnumerable<PreferenceSpec> specs)
        {
            var current = await repo.ListAsync();
            var created = 0;
            foreach (var spec in specs)
            {
                var existing = current.FirstOrDefault(p => p.Name == spec.Name);
                if (existing == null)
                {
                    var defaultVal = spec.Type == "json"
                        ? JsonUtils.SerializePretty(JsonUtils.ToNode(spec.DefaultValue))
                        : JsonUtils.ToStoredString(spec.DefaultValue);
                    await SavePreferenceAsync(new Preference
                    {
                        Name = spec.Name,
                        Type = spec.Type,
                        DefaultValue = defaultVal,
                        GroupName = spec.GroupName,
                        Notes = spec.Notes ?? "",
                        LastUpdatedBy = "hoist-bootstrap"
                    });
                    logger.LogWarning("Required preference {Name} missing and created with default value | verify default is appropriate for this application", spec.Name);
                    created++;
                }
                else if (existing.Type != spec.Type)
                {
                    logger.LogError("Unexpected value type for required preference {Name} | expected {Expected} got {Actual} | review and fix!", spec.Name, spec.Type, existing.Type);
                }
            }
            logger.LogDebug("Validated presence of {Count} required prefs | created {Created}", specs.Count(), created);
        }

        //------------------------
        // Value logic and validation (Preference / UserPreference)
        //------------------------
        /// <summary>externalDefaultValue / externalUserValue: the string parsed according to the pref type.</summary>
        public static JsonNode ParseValue(string type, string value, bool jsonAsObject)
        {
            return type switch
            {
                "json" => jsonAsObject ? JsonUtils.ParseObjectOrArray(value) : JsonValue.Create(value),
                "int" => JsonValue.Create(GroovyStrings.ToInteger(value)),
                "long" => JsonValue.Create(GroovyStrings.ToLong(value)),
                "double" => JsonValue.Create(GroovyStrings.ToDouble(value)),
                "bool" => JsonValue.Create(GroovyStrings.ToBoolean(value)),
                _ => JsonValue.Create(value)
            };
        }

        /// <summary>Preference.isValidForType - true when the string is valid for the type.</summary>
        public static bool IsValidForType(string type, string value)
        {
            if (value == null) return false;
            return type switch
            {
                "bool" => value == "true" || value == "false",
                "int" => GroovyStrings.IsInteger(value),
                "long" => GroovyStrings.IsLong(value),
                "double" => GroovyStrings.IsDouble(value),
                "json" => JsonUtils.IsJson(value),
                _ => true
            };
        }

        public async Task<Preference> SavePreferenceAsync(Preference pref)
        {
            const string cls = "io.xh.hoist.pref.Preference";
            var errors = new List<string>();
            if (string.IsNullOrEmpty(pref.Name)) errors.Add(pref.Name == null ? GrailsMessages.Null("name", cls) : GrailsMessages.Blank("name", cls));
            else if (pref.Name.Length > 50) errors.Add(GrailsMessages.MaxSize("name", cls, pref.Name, 50));
            else if (await repo.NameExistsAsync(pref.Name, pref.Id)) errors.Add(GrailsMessages.NotUnique("name", cls, pref.Name));
            if (!Preference.Types.Contains(pref.Type)) errors.Add(GrailsMessages.NotInList("type", cls, pref.Type, Preference.Types));
            if (pref.DefaultValue == null) errors.Add(GrailsMessages.Null("defaultValue", cls));
            else if (!IsValidForType(pref.Type, pref.DefaultValue)) errors.Add(GrailsMessages.CustomValidation("defaultValue", cls, pref.DefaultValue));
            if (pref.Notes?.Length > 1200) errors.Add(GrailsMessages.MaxSize("notes", cls, pref.Notes, 1200));
            if (pref.LastUpdatedBy?.Length > 50) errors.Add(GrailsMessages.MaxSize("lastUpdatedBy", cls, pref.LastUpdatedBy, 50));
            if (string.IsNullOrWhiteSpace(pref.GroupName)) errors.Add(GrailsMessages.Blank("groupName", cls));
            if (errors.Count > 0) throw new ValidationException(errors);
            return await repo.SaveAsync(pref);
        }

        public async Task<UserPreference> SaveUserPreferenceAsync(UserPreference userPref, Preference pref)
        {
            const string cls = "io.xh.hoist.pref.UserPreference";
            var errors = new List<string>();
            if (string.IsNullOrEmpty(userPref.Username)) errors.Add(GrailsMessages.Blank("username", cls));
            else if (userPref.Username.Length > 50) errors.Add(GrailsMessages.MaxSize("username", cls, userPref.Username, 50));
            else if (await repo.UserPrefExistsAsync(pref.Id, userPref.Username, userPref.Id)) errors.Add(GrailsMessages.NotUnique("username", cls, userPref.Username));
            if (userPref.UserValue == null) errors.Add(GrailsMessages.Null("userValue", cls));
            else if (!IsValidForType(pref.Type, userPref.UserValue)) errors.Add(GrailsMessages.CustomValidation("userValue", cls, userPref.UserValue));
            if (userPref.LastUpdatedBy?.Length > 50) errors.Add(GrailsMessages.MaxSize("lastUpdatedBy", cls, userPref.LastUpdatedBy, 50));
            if (errors.Count > 0) throw new ValidationException(errors);

            userPref.PreferenceId = pref.Id;
            return await repo.SaveUserPrefAsync(userPref);
        }

        //------------------------
        // Admin formats (formatForJSON) - json values stay strings
        //------------------------
        public static Dictionary<string, object> FormatForAdmin(Preference p) => new Dictionary<string, object>
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["groupName"] = p.GroupName,
            ["type"] = p.Type,
            ["defaultValue"] = SafeParse(p.Type, p.DefaultValue),
            ["notes"] = p.Notes,
            ["lastUpdatedBy"] = p.LastUpdatedBy,
            ["lastUpdated"] = p.LastUpdated
        };

        public static Dictionary<string, object> FormatForAdmin(UserPreference u) => new Dictionary<string, object>
        {
            ["id"] = u.Id,
            ["name"] = u.Preference?.Name,
            ["groupName"] = u.Preference?.GroupName,
            ["type"] = u.Preference?.Type,
            ["username"] = u.Username,
            ["userValue"] = SafeParse(u.Preference?.Type, u.UserValue),
            ["lastUpdatedBy"] = u.LastUpdatedBy,
            ["lastUpdated"] = u.LastUpdated
        };

        private static JsonNode SafeParse(string type, string value)
        {
            try
            {
                return ParseValue(type, value, jsonAsObject: false);
            }
            catch (Exception)
            {
                return JsonValue.Create(value);
            }
        }

        //------------------------
        // Implementation
        //------------------------
        private static Dictionary<string, object> FormatForClient(Preference pref, UserPreference userPref)
        {
            return new Dictionary<string, object>
            {
                ["type"] = pref.Type,
                ["value"] = userPref != null ? ParseValue(pref.Type, userPref.UserValue, true) : ParseValue(pref.Type, pref.DefaultValue, true),
                ["defaultValue"] = ParseValue(pref.Type, pref.DefaultValue, true),
                ["isSet"] = userPref != null
            };
        }

        private async Task<JsonNode> GetUserPreferenceAsync(string key, string type, string username)
        {
            var pref = await GetDefaultPreferenceAsync(key, type);
            var userPref = await repo.FindUserPrefAsync(pref.Id, username ?? identity.Username);
            return userPref != null ? ParseValue(pref.Type, userPref.UserValue, true) : ParseValue(pref.Type, pref.DefaultValue, true);
        }

        private async Task SetUserPreferenceAsync(string key, string value, string type, string username)
        {
            username ??= identity.Username;
            var pref = await GetDefaultPreferenceAsync(key, type);
            var userPref = await repo.FindUserPrefAsync(pref.Id, username) ?? new UserPreference { Username = username };
            userPref.UserValue = value;
            userPref.LastUpdatedBy = identity.AuthUsername;
            await SaveUserPreferenceAsync(userPref, pref);
        }

        private async Task<Preference> GetDefaultPreferenceAsync(string key, string type)
        {
            var pref = await repo.FindByNameAsync(key) ?? throw new HoistException("Preference not found: " + key);
            if (type != null && pref.Type != type) throw new HoistException("Unexpected type for preference: " + key);
            return pref;
        }
    }
}
