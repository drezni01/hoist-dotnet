using System.Text.Json.Nodes;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hoist.Core.Services
{
    /// <summary>
    /// Port of hoist-core jsonblob/JsonBlobService (v42 access rules). Read access: acl '*' or owner.
    /// Write access: the owner only; global (null-owner) blobs need a role from
    /// xhJsonBlobConfig.globalWriteRoles[type] (or ['*'] fallback). Type is fixed at creation.
    /// Operations default to the current (apparent) user.
    /// </summary>
    public class JsonBlobService
    {
        private static readonly string[] UpdatableFields = { "name", "description", "value", "meta", "owner", "acl" };
        private static readonly string[] CreatableFields = { "type", "name", "description", "value", "meta", "owner", "acl" };

        private readonly IJsonBlobRepository repo;
        private readonly ConfigService configService;
        private readonly IHoistUserService userService;
        private readonly IRoleService roleService;
        private readonly IIdentityAccessor identity;
        private readonly ILogger<JsonBlobService> logger;

        public JsonBlobService(
            IJsonBlobRepository repo,
            ConfigService configService,
            IHoistUserService userService,
            IRoleService roleService,
            IIdentityAccessor identity,
            ILogger<JsonBlobService> logger)
        {
            this.repo = repo;
            this.configService = configService;
            this.userService = userService;
            this.roleService = roleService;
            this.identity = identity;
            this.logger = logger;
        }

        public async Task<JsonBlob> GetAsync(string token, string username = null)
        {
            username ??= identity.Username;
            var ret = await repo.FindByTokenAsync(token);
            if (ret == null || ret.ArchivedDate != 0) throw new HoistException($"Active JsonBlob not found with token '{token}'");
            EnsureReadable(ret, username);
            return ret;
        }

        public async Task<JsonBlob> FindAsync(string type, string name, string owner, string username = null)
        {
            username ??= identity.Username;
            var ret = (await repo.ListActiveAsync(type)).FirstOrDefault(b => b.Name == name && b.Owner == owner);
            if (ret != null) EnsureReadable(ret, username);
            return ret;
        }

        /// <summary>Active blobs of the type the user owns or that are shared (acl '*').</summary>
        public async Task<List<JsonBlob>> ListAsync(string type, string username = null)
        {
            username ??= identity.Username;
            return (await repo.ListActiveAsync(type)).Where(b => b.Owner == username || b.Acl == "*").OrderBy(b => b.Id).ToList();
        }

        public async Task<List<string>> ListTokensAsync(string type, string username = null) => (await ListAsync(type, username)).Select(b => b.Token).ToList();

        public Task<int> DeleteByNameAndOwnerAsync(string name, string owner) => repo.DeleteByNameAndOwnerAsync(name, owner);

        public async Task<JsonBlob> CreateAsync(JsonObject data, string username = null)
        {
            username ??= identity.Username;
            var fields = new Dictionary<string, JsonNode> { ["owner"] = JsonValue.Create(username) };
            foreach (var (k, v) in data)
            {
                if (CreatableFields.Contains(k)) fields[k] = v?.DeepClone();
            }

            var blob = new JsonBlob
            {
                Type = Str(fields.GetValueOrDefault("type")),
                Name = Str(fields.GetValueOrDefault("name")),
                Description = Str(fields.GetValueOrDefault("description")),
                Owner = Str(fields.GetValueOrDefault("owner")),
                Acl = Str(fields.GetValueOrDefault("acl")),
                Value = fields.ContainsKey("value") ? JsonUtils.Serialize(fields["value"]) : null,
                Meta = fields.ContainsKey("meta") ? JsonUtils.Serialize(fields["meta"]) : null,
                LastUpdatedBy = username
            };
            await EnsureWritableAsync(blob.Type, blob.Owner, username);
            return await SaveAsync(blob);
        }

        public async Task<JsonBlob> UpdateAsync(string token, JsonObject data, string username = null)
        {
            username ??= identity.Username;
            var blob = await GetAsync(token, username);
            await EnsureWritableAsync(blob.Type, blob.Owner, username);
            return await UpdateInternalAsync(blob, data, username);
        }

        public async Task<JsonBlob> CreateOrUpdateAsync(string type, string name, JsonObject data, string username = null)
        {
            username ??= identity.Username;
            var blob = await FindAsync(type, name, username, username);
            if (blob != null) return await UpdateInternalAsync(blob, data, username);

            var create = (JsonObject)data.DeepClone();
            create["type"] = type;
            create["name"] = name;
            return await CreateAsync(create, username);
        }

        public async Task<JsonBlob> ArchiveAsync(string token, string username = null)
        {
            username ??= identity.Username;
            var blob = await GetAsync(token, username);
            await EnsureWritableAsync(blob.Type, blob.Owner, username);
            blob.ArchivedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            blob.LastUpdatedBy = identity.AuthUsername ?? username;
            return await SaveAsync(blob);
        }

        /// <summary>Rewrites meta.group for blobs in the group or below it (from + '/'). Returns the count updated.</summary>
        public async Task<int> RenameGroupAsync(string type, string ownerName, string from, string to, string username = null)
        {
            username ??= identity.Username;
            from = from?.Trim();
            to = to?.Trim();
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                throw new HoistException("Group rename requires both 'from' and 'to' group paths");
            }
            await EnsureWritableAsync(type, ownerName, username);

            var count = 0;
            foreach (var blob in (await repo.ListActiveAsync(type)).Where(b => b.Owner == ownerName))
            {
                JsonObject meta;
                try
                {
                    meta = JsonUtils.ParseObjectOrArray(blob.Meta) as JsonObject;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Skipping blob with unparsable meta in group rename | token {Token}", blob.Token);
                    continue;
                }
                if (meta?["group"] is not JsonValue gv || !gv.TryGetValue<string>(out var group)) continue;
                if (group != from && !group.StartsWith(from + "/", StringComparison.Ordinal)) continue;

                var renamed = to + group.Substring(from.Length);
                if (renamed == group) continue;
                meta["group"] = renamed;
                blob.Meta = JsonUtils.Serialize(meta);
                blob.LastUpdatedBy = username;
                await SaveAsync(blob);
                count++;
            }
            logger.LogInformation("Renamed group | type {Type} from {From} to {To} updated {Count}", type, from, to, count);
            return count;
        }

        public async Task<bool> CanWriteGlobalAsync(string type, string username)
        {
            var conf = await configService.GetObjectAsync("JsonBlobConfig");
            var roles = conf["globalWriteRoles"] as JsonObject ?? new JsonObject();
            var required = (roles.ContainsKey(type) ? roles[type] : roles["*"]) as JsonArray;
            if (required == null || required.Count == 0) return false;

            var names = required.Select(r => r?.ToString()).ToList();
            if (names.Contains("*")) return true;

            var user = await userService.FindAsync(username);
            if (user == null) return false;
            var held = await roleService.GetRolesForUserAsync(user.Username);
            return names.Any(held.Contains);
        }

        //------------------------
        // Client format
        //------------------------
        /// <summary>JsonBlob.formatForClient: meta and value parsed; value only when includeValue.</summary>
        public static Dictionary<string, object> FormatForClient(JsonBlob b, bool includeValue = true)
        {
            var ret = new Dictionary<string, object>
            {
                ["id"] = b.Id,
                ["token"] = b.Token,
                ["type"] = b.Type,
                ["owner"] = b.Owner,
                ["acl"] = b.Acl,
                ["name"] = b.Name,
                ["archived"] = b.ArchivedDate > 0,
                ["archivedDate"] = b.ArchivedDate,
                ["description"] = b.Description,
                ["dateCreated"] = b.DateCreated,
                ["lastUpdated"] = b.LastUpdated,
                ["lastUpdatedBy"] = b.LastUpdatedBy,
                ["meta"] = JsonUtils.ParseObjectOrArray(b.Meta)
            };
            if (includeValue) ret["value"] = JsonUtils.ParseObjectOrArray(b.Value);
            return ret;
        }

        /// <summary>JsonBlob.formatForJSON (admin): value and meta as raw strings.</summary>
        public static Dictionary<string, object> FormatForAdmin(JsonBlob b) => new Dictionary<string, object>
        {
            ["id"] = b.Id,
            ["token"] = b.Token,
            ["type"] = b.Type,
            ["owner"] = b.Owner,
            ["acl"] = b.Acl,
            ["name"] = b.Name,
            ["value"] = b.Value,
            ["meta"] = b.Meta,
            ["description"] = b.Description,
            ["archived"] = b.ArchivedDate > 0,
            ["archivedDate"] = b.ArchivedDate,
            ["dateCreated"] = b.DateCreated,
            ["lastUpdated"] = b.LastUpdated,
            ["lastUpdatedBy"] = b.LastUpdatedBy
        };

        //------------------------
        // Validation + save (GORM constraints)
        //------------------------
        public async Task<JsonBlob> SaveAsync(JsonBlob b)
        {
            const string cls = "io.xh.hoist.jsonblob.JsonBlob";
            var errors = new List<string>();
            if (string.IsNullOrEmpty(b.Token)) errors.Add(GrailsMessages.Blank("token", cls));
            if (string.IsNullOrWhiteSpace(b.Type)) errors.Add(b.Type == null ? GrailsMessages.Null("type", cls) : GrailsMessages.Blank("type", cls));
            else if (b.Type.Length > 50) errors.Add(GrailsMessages.MaxSize("type", cls, b.Type, 50));
            if (b.Owner != null && b.Owner.Trim().Length == 0) errors.Add(GrailsMessages.Blank("owner", cls));
            else if (b.Owner?.Length > 50) errors.Add(GrailsMessages.MaxSize("owner", cls, b.Owner, 50));
            if (string.IsNullOrWhiteSpace(b.Name)) errors.Add(b.Name == null ? GrailsMessages.Null("name", cls) : GrailsMessages.Blank("name", cls));
            else if (b.Name.Length > 255) errors.Add(GrailsMessages.MaxSize("name", cls, b.Name, 255));
            else if (await repo.NameExistsAsync(b)) errors.Add(GrailsMessages.NotUnique("name", cls, b.Name));
            if (b.Value == null) errors.Add(GrailsMessages.Null("value", cls));
            else if (!JsonUtils.IsJson(b.Value)) errors.Add(GrailsMessages.CustomValidation("value", cls, b.Value));
            if (!JsonUtils.IsJson(b.Meta)) errors.Add(GrailsMessages.CustomValidation("meta", cls, b.Meta));
            if (errors.Count > 0) throw new ValidationException(errors);
            return await repo.SaveAsync(b);
        }

        //------------------------
        // Implementation
        //------------------------
        private async Task<JsonBlob> UpdateInternalAsync(JsonBlob blob, JsonObject data, string username)
        {
            if (data == null || data.Count == 0) return blob;

            if (data.TryGetPropertyValue("owner", out var ownerNode))
            {
                var newOwner = Str(ownerNode);
                if (newOwner != blob.Owner) await EnsureWritableAsync(blob.Type, newOwner, username);
                blob.Owner = newOwner;
            }
            if (data.TryGetPropertyValue("name", out var name)) blob.Name = Str(name);
            if (data.TryGetPropertyValue("description", out var description)) blob.Description = Str(description);
            if (data.TryGetPropertyValue("acl", out var acl)) blob.Acl = Str(acl);
            if (data.TryGetPropertyValue("value", out var value)) blob.Value = JsonUtils.Serialize(value);
            if (data.TryGetPropertyValue("meta", out var meta)) blob.Meta = JsonUtils.Serialize(meta);
            blob.LastUpdatedBy = username;
            return await SaveAsync(blob);
        }

        private static void EnsureReadable(JsonBlob blob, string username)
        {
            if (!(blob.Acl == "*" || blob.Owner == username))
            {
                throw new NotAuthorizedException($"User '{username}' does not have access to JsonBlob with token '{blob.Token}'");
            }
        }

        private async Task EnsureWritableAsync(string type, string owner, string username)
        {
            var ok = owner == username || (owner == null && await CanWriteGlobalAsync(type, username));
            if (!ok)
            {
                throw new NotAuthorizedException(owner == null
                    ? $"User '{username}' does not have write access to global JsonBlobs of type '{type}'"
                    : $"User '{username}' does not have write access to JsonBlobs owned by '{owner}'");
            }
        }

        private static string Str(JsonNode node) => node == null ? null : JsonUtils.ToStoredString(node);
    }
}
