using System.Text.Json.Nodes;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Hoist.Api.Controllers.Admin
{
    /// <summary>Port of admin/ConfigAdminController - rest/configAdmin.</summary>
    [Route("rest/configAdmin")]
    public class ConfigAdminController : AdminRestController<AppConfig>
    {
        private readonly IConfigRepository repo;
        private readonly ConfigService configService;

        public ConfigAdminController(IConfigRepository repo, ConfigService configService)
        {
            this.repo = repo;
            this.configService = configService;
        }

        protected override string EntityName => "AppConfig";
        protected override long IdOf(AppConfig obj) => obj.Id;
        protected override object Format(AppConfig obj) => configService.FormatForAdmin(obj);
        protected override Task<AppConfig> GetAsync(long id) => repo.FindByIdAsync(id);

        protected override async Task<List<AppConfig>> DoListAsync(JsonObject query)
        {
            if (query.Count > 0) throw new HoistException("You must implement an override of doList() to use query fields.");
            return await repo.ListAsync();
        }

        protected override async Task<object> LookupDataAsync()
        {
            var configs = await repo.ListAsync();
            return new
            {
                valueTypes = AppConfig.Types,
                groupNames = configs.Select(c => c.GroupName).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList()
            };
        }

        protected override Task<AppConfig> DoCreateAsync(JsonObject data)
        {
            var config = new AppConfig();
            BindConfig(config, data);
            return configService.SaveAsync(config, valueChanged: true);
        }

        protected override Task<AppConfig> DoUpdateAsync(AppConfig obj, JsonObject data)
        {
            var oldValue = obj.Value;
            var oldDigest = obj.ValueType == "pwd" ? configService.DigestOfStoredPwd(obj) : null;
            BindConfig(obj, data);

            // The admin form posts back the digest shown for a pwd config - keep the stored secret then.
            if (obj.ValueType == "pwd" && oldDigest != null && obj.Value == oldDigest) obj.Value = oldValue;
            return configService.SaveAsync(obj, valueChanged: obj.Value != oldValue);
        }

        protected override Task DoDeleteAsync(AppConfig obj) => repo.DeleteAsync(obj);

        private static void BindConfig(AppConfig c, JsonObject data)
        {
            Bind(data, "name", v => c.Name = v);
            Bind(data, "groupName", v => c.GroupName = v);
            Bind(data, "valueType", v => c.ValueType = v);
            Bind(data, "value", v => c.Value = v);
            BindBool(data, "clientVisible", v => c.ClientVisible = v);
            Bind(data, "note", v => c.Note = v);
            Bind(data, "lastUpdatedBy", v => c.LastUpdatedBy = v);
        }
    }

    /// <summary>Port of admin/PreferenceAdminController - rest/preferenceAdmin.</summary>
    [Route("rest/preferenceAdmin")]
    public class PreferenceAdminController : AdminRestController<Preference>
    {
        private readonly IPrefRepository repo;
        private readonly PrefService prefService;

        public PreferenceAdminController(IPrefRepository repo, PrefService prefService)
        {
            this.repo = repo;
            this.prefService = prefService;
        }

        protected override string EntityName => "Preference";
        protected override long IdOf(Preference obj) => obj.Id;
        protected override object Format(Preference obj) => PrefService.FormatForAdmin(obj);
        protected override Task<Preference> GetAsync(long id) => repo.FindByIdAsync(id);

        protected override async Task<List<Preference>> DoListAsync(JsonObject query)
        {
            if (query.Count > 0) throw new HoistException("You must implement an override of doList() to use query fields.");
            return await repo.ListAsync();
        }

        protected override async Task<object> LookupDataAsync()
        {
            var prefs = await repo.ListAsync();
            return new
            {
                types = Preference.Types,
                groupNames = prefs.Select(p => p.GroupName).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList()
            };
        }

        protected override Task<Preference> DoCreateAsync(JsonObject data)
        {
            var pref = new Preference();
            BindPref(pref, data);
            return prefService.SavePreferenceAsync(pref);
        }

        protected override Task<Preference> DoUpdateAsync(Preference obj, JsonObject data)
        {
            BindPref(obj, data);
            return prefService.SavePreferenceAsync(obj);
        }

        protected override Task DoDeleteAsync(Preference obj) => repo.DeleteAsync(obj);

        private static void BindPref(Preference p, JsonObject data)
        {
            Bind(data, "name", v => p.Name = v);
            Bind(data, "groupName", v => p.GroupName = v);
            Bind(data, "type", v => p.Type = v);
            Bind(data, "defaultValue", v => p.DefaultValue = v);
            Bind(data, "notes", v => p.Notes = v);
            Bind(data, "lastUpdatedBy", v => p.LastUpdatedBy = v);
        }
    }

    /// <summary>Port of admin/UserPreferenceAdminController - rest/userPreferenceAdmin.</summary>
    [Route("rest/userPreferenceAdmin")]
    public class UserPreferenceAdminController : AdminRestController<UserPreference>
    {
        private readonly IPrefRepository repo;
        private readonly PrefService prefService;

        public UserPreferenceAdminController(IPrefRepository repo, PrefService prefService)
        {
            this.repo = repo;
            this.prefService = prefService;
        }

        protected override string EntityName => "UserPreference";
        protected override long IdOf(UserPreference obj) => obj.Id;
        protected override object Format(UserPreference obj) => PrefService.FormatForAdmin(obj);
        protected override Task<UserPreference> GetAsync(long id) => repo.FindUserPrefByIdAsync(id);

        /// <summary>query.name: exact pref name; query.username: substring match (SQL LIKE %x%).</summary>
        protected override Task<List<UserPreference>> DoListAsync(JsonObject query)
        {
            return repo.QueryUserPrefsAsync(query["name"]?.ToString(), query["username"]?.ToString());
        }

        protected override async Task<object> LookupDataAsync()
        {
            var prefs = await repo.ListAsync();
            return new { names = prefs.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList() };
        }

        protected override async Task<UserPreference> DoCreateAsync(JsonObject data)
        {
            var userPref = new UserPreference();
            var pref = await BindUserPrefAsync(userPref, data) ?? throw new ValidationException("Property [preference] of class [class io.xh.hoist.pref.UserPreference] cannot be null");
            await prefService.SaveUserPreferenceAsync(userPref, pref);
            return await repo.FindUserPrefByIdAsync(userPref.Id);
        }

        protected override async Task<UserPreference> DoUpdateAsync(UserPreference obj, JsonObject data)
        {
            var pref = await BindUserPrefAsync(obj, data) ?? obj.Preference;
            await prefService.SaveUserPreferenceAsync(obj, pref);
            return await repo.FindUserPrefByIdAsync(obj.Id);
        }

        protected override Task DoDeleteAsync(UserPreference obj) => repo.DeleteUserPrefAsync(obj);

        /// <summary>preprocessSubmit: data.name selects the preference. Returns the selected preference, if any.</summary>
        private async Task<Preference> BindUserPrefAsync(UserPreference u, JsonObject data)
        {
            Preference pref = null;
            var name = data["name"]?.ToString();
            if (!string.IsNullOrEmpty(name)) pref = await repo.FindByNameAsync(name);
            Bind(data, "username", v => u.Username = v);
            Bind(data, "userValue", v => u.UserValue = v);
            Bind(data, "lastUpdatedBy", v => u.LastUpdatedBy = v);
            return pref;
        }
    }

    /// <summary>Port of admin/JsonBlobAdminController - rest/jsonBlobAdmin. value and meta are edited as raw strings.</summary>
    [Route("rest/jsonBlobAdmin")]
    public class JsonBlobAdminController : AdminRestController<JsonBlob>
    {
        private readonly IJsonBlobRepository repo;
        private readonly JsonBlobService blobService;

        public JsonBlobAdminController(IJsonBlobRepository repo, JsonBlobService blobService)
        {
            this.repo = repo;
            this.blobService = blobService;
        }

        protected override string EntityName => "JsonBlob";
        protected override long IdOf(JsonBlob obj) => obj.Id;
        protected override object Format(JsonBlob obj) => JsonBlobService.FormatForAdmin(obj);
        protected override Task<JsonBlob> GetAsync(long id) => repo.FindByIdAsync(id);

        protected override async Task<List<JsonBlob>> DoListAsync(JsonObject query)
        {
            if (query.Count > 0) throw new HoistException("You must implement an override of doList() to use query fields.");
            return await repo.ListAllAsync();
        }

        protected override async Task<object> LookupDataAsync()
        {
            var blobs = await repo.ListAllAsync();
            return new { types = blobs.Select(b => b.Type).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList() };
        }

        /// <summary>archived true/false (explicitly) sets or clears archivedDate.</summary>
        protected override void PreprocessSubmit(JsonObject data)
        {
            if (data["archived"] is JsonValue v && v.TryGetValue<bool>(out var archived))
            {
                data["archivedDate"] = archived ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
            }
            base.PreprocessSubmit(data);
        }

        protected override Task<JsonBlob> DoCreateAsync(JsonObject data)
        {
            var blob = new JsonBlob();
            BindBlob(blob, data);
            return blobService.SaveAsync(blob);
        }

        protected override Task<JsonBlob> DoUpdateAsync(JsonBlob obj, JsonObject data)
        {
            BindBlob(obj, data);
            return blobService.SaveAsync(obj);
        }

        protected override Task DoDeleteAsync(JsonBlob obj) => repo.DeleteAsync(obj);

        private static void BindBlob(JsonBlob b, JsonObject data)
        {
            Bind(data, "token", v => { if (!string.IsNullOrEmpty(v)) b.Token = v; });
            Bind(data, "type", v => b.Type = v);
            Bind(data, "owner", v => b.Owner = v);
            Bind(data, "acl", v => b.Acl = v);
            Bind(data, "name", v => b.Name = v);
            Bind(data, "value", v => b.Value = v);
            Bind(data, "meta", v => b.Meta = v);
            Bind(data, "description", v => b.Description = v);
            Bind(data, "lastUpdatedBy", v => b.LastUpdatedBy = v);
            if (data["archivedDate"] is JsonValue ad && ToLong(ad) is long archivedDate) b.ArchivedDate = archivedDate;
        }
    }
}
