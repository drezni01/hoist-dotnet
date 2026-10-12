using System.Text.Json.Nodes;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hoist.Core.Services
{
    /// <summary>
    /// Port of hoist-core view/ViewService - ViewManager views are JsonBlobs of the ViewManager's type.
    /// Per-user state lives in a sidecar blob of the same type named xhUserState:
    /// {currentView: {viewInstance: token}, userPinned: {token: bool}, autoSave: bool}.
    /// </summary>
    public class ViewService
    {
        public const string StateBlobName = "xhUserState";

        private readonly JsonBlobService blobService;
        private readonly TrackService trackService;
        private readonly IIdentityAccessor identity;
        private readonly ILogger<ViewService> logger;

        public ViewService(JsonBlobService blobService, TrackService trackService, IIdentityAccessor identity, ILogger<ViewService> logger)
        {
            this.blobService = blobService;
            this.trackService = trackService;
            this.identity = identity;
            this.logger = logger;
        }

        //------------------------
        // ViewManager state + support
        //------------------------
        public async Task<Dictionary<string, object>> GetAllDataAsync(string type, string viewInstance, string username = null)
        {
            username ??= identity.Username;
            var blobs = await blobService.ListAsync(type, username);
            var rawState = blobs.FirstOrDefault(b => b.Name == StateBlobName);
            var views = blobs.Where(b => b.Name != StateBlobName);
            return new Dictionary<string, object>
            {
                ["state"] = GetStateFromBlob(rawState, viewInstance),
                ["views"] = views.Select(v => JsonBlobService.FormatForClient(v, includeValue: false)).ToList(),
                ["manageGlobal"] = await blobService.CanWriteGlobalAsync(type, username)
            };
        }

        public async Task<JsonObject> UpdateStateAsync(string type, string viewInstance, JsonObject update, string username = null)
        {
            username ??= identity.Username;
            var currBlob = await blobService.FindAsync(type, StateBlobName, username, username);
            var currValue = JsonUtils.ParseObjectOrArray(currBlob?.Value) as JsonObject;
            var newValue = new JsonObject
            {
                ["currentView"] = (currValue?["currentView"] as JsonObject)?.DeepClone() ?? new JsonObject(),
                ["userPinned"] = (currValue?["userPinned"] as JsonObject)?.DeepClone() ?? new JsonObject(),
                ["autoSave"] = IsTrue(currValue?["autoSave"])
            };

            if (update.ContainsKey("currentView")) newValue["currentView"]![viewInstance] = update["currentView"]?.DeepClone();
            if (update.ContainsKey("userPinned") && update["userPinned"] is JsonObject pinnedUpdate)
            {
                var pinned = newValue["userPinned"].AsObject();
                foreach (var (k, v) in pinnedUpdate) pinned[k] = v?.DeepClone();
            }
            if (update.ContainsKey("autoSave")) newValue["autoSave"] = update["autoSave"]?.DeepClone();

            // Keep only pins for views that still exist.
            var userPinned = newValue["userPinned"].AsObject();
            if (userPinned.Count > 0)
            {
                var tokens = (await blobService.ListTokensAsync(type, username)).ToHashSet();
                foreach (var key in userPinned.Select(kv => kv.Key).ToList())
                {
                    if (!tokens.Contains(key)) userPinned.Remove(key);
                }
            }

            var blob = await blobService.CreateOrUpdateAsync(type, StateBlobName, new JsonObject { ["value"] = newValue }, username);
            return GetStateFromBlob(blob, viewInstance);
        }

        /// <summary>Deletes the user's view state for every ViewManager type (xh/clearUserState).</summary>
        public Task ClearAllStateAsync(string username = null) => blobService.DeleteByNameAndOwnerAsync(StateBlobName, username ?? identity.Username);

        //------------------------
        // Individual view management
        //------------------------
        public async Task<Dictionary<string, object>> GetAsync(string token, string username = null)
        {
            return JsonBlobService.FormatForClient(await blobService.GetAsync(token, username ?? identity.Username), includeValue: true);
        }

        public async Task<Dictionary<string, object>> CreateAsync(JsonObject data, string username = null)
        {
            username ??= identity.Username;
            var isShared = IsTrue(data["isShared"]);
            var isGlobal = IsTrue(data["isGlobal"]);
            var meta = isGlobal
                ? new JsonObject { ["group"] = data["group"]?.DeepClone() }
                : new JsonObject { ["group"] = data["group"]?.DeepClone(), ["isShared"] = data["isShared"]?.DeepClone() };

            var ret = await blobService.CreateAsync(new JsonObject
            {
                ["type"] = data["type"]?.DeepClone(),
                ["name"] = data["name"]?.DeepClone(),
                ["description"] = data["description"]?.DeepClone(),
                ["owner"] = isGlobal ? null : username,
                ["acl"] = isGlobal || isShared ? "*" : null,
                ["meta"] = meta,
                ["value"] = data["value"]?.DeepClone()
            }, username);

            if (data.ContainsKey("isPinned")) await UpdatePinnedAsync(ret.Type, new[] { ret.Token }, data["isPinned"], username);
            await TrackChangeAsync("Created View", ret);
            return JsonBlobService.FormatForClient(ret, includeValue: true);
        }

        public async Task<Dictionary<string, object>> UpdateInfoAsync(string token, JsonObject data, string username = null)
        {
            username ??= identity.Username;
            var ret = await DoUpdateInfoAsync(token, data, username);
            if (data.ContainsKey("isPinned")) await UpdatePinnedAsync(ret.Type, new[] { ret.Token }, data["isPinned"], username);
            await TrackChangeAsync("Updated View Info", ret);
            return JsonBlobService.FormatForClient(ret, includeValue: true);
        }

        public async Task BulkUpdateInfoAsync(List<string> tokens, JsonObject data, string username = null)
        {
            username ??= identity.Username;
            var failures = new Dictionary<string, Exception>();
            var updated = new List<JsonBlob>();
            foreach (var token in tokens)
            {
                try
                {
                    updated.Add(await DoUpdateInfoAsync(token, data, username));
                }
                catch (Exception e)
                {
                    failures[token] = e;
                    logger.LogError(e, "Failed to update View info | token {Token}", token);
                }
            }

            if (updated.Count > 0)
            {
                // Pinned state is stored per view type, so update each type's state blob just once.
                if (data.ContainsKey("isPinned"))
                {
                    foreach (var group in updated.GroupBy(b => b.Type))
                    {
                        await UpdatePinnedAsync(group.Key, group.Select(b => b.Token), data["isPinned"], username);
                    }
                }
                await TrackChangeAsync("Bulk Updated View Info", new JsonObject { ["count"] = updated.Count });
            }

            if (failures.Count > 0)
            {
                var detail = string.Join("; ", failures.Select(f => $"{f.Key}: {f.Value.Message}"));
                throw BulkFailure($"Failed to update {failures.Count} of {tokens.Count} view(s) - {detail}", failures.Values.ToList());
            }
        }

        public async Task<Dictionary<string, object>> UpdateValueAsync(string token, JsonNode value, string username = null)
        {
            username ??= identity.Username;
            var ret = await blobService.UpdateAsync(token, new JsonObject { ["value"] = value?.DeepClone() }, username);
            if (ret.Owner == null) await TrackChangeAsync("Updated Global View definition", ret);
            return JsonBlobService.FormatForClient(ret, includeValue: true);
        }

        public async Task DeleteAsync(List<string> tokens, string username = null)
        {
            username ??= identity.Username;
            var failures = new List<Exception>();
            foreach (var token in tokens)
            {
                try
                {
                    await blobService.ArchiveAsync(token, username);
                }
                catch (Exception e)
                {
                    failures.Add(e);
                    logger.LogError(e, "Failed to delete View | token {Token}", token);
                }
            }

            var successCount = tokens.Count - failures.Count;
            if (successCount > 0) await TrackChangeAsync("Deleted Views", new JsonObject { ["count"] = successCount });
            if (failures.Count > 0)
            {
                throw BulkFailure($"Failed to delete {failures.Count} view(s) - {failures[0].Message}", failures);
            }
        }

        public async Task<Dictionary<string, object>> RenameGroupAsync(string type, string from, string to, bool isGlobal, string username = null)
        {
            username ??= identity.Username;
            var count = await blobService.RenameGroupAsync(type, isGlobal ? null : username, from, to, username);
            await TrackChangeAsync("Renamed View Group", new JsonObject
            {
                ["type"] = type,
                ["from"] = from,
                ["to"] = to,
                ["isGlobal"] = isGlobal,
                ["count"] = count
            });
            return new Dictionary<string, object> { ["count"] = count };
        }

        //------------------------
        // Implementation
        //------------------------
        private async Task<JsonBlob> DoUpdateInfoAsync(string token, JsonObject data, string username)
        {
            var existing = await blobService.GetAsync(token, username);
            var meta = JsonUtils.ParseObjectOrArray(existing.Meta) as JsonObject ?? new JsonObject();
            var core = new JsonObject();

            foreach (var (k, v) in data)
            {
                if (k == "name" || k == "description") core[k] = v?.DeepClone();
                else if (k == "group") meta[k] = v?.DeepClone();
            }

            if (data.ContainsKey("isGlobal") || data.ContainsKey("isShared"))
            {
                if (IsTrue(data["isGlobal"]))
                {
                    meta.Remove("isShared");
                    core["owner"] = null;
                    core["acl"] = "*";
                }
                else if (IsTrue(data["isShared"]))
                {
                    meta["isShared"] = data["isShared"]?.DeepClone();
                    core["owner"] = username;
                    core["acl"] = "*";
                }
                else
                {
                    meta["isShared"] = false;
                    core["owner"] = existing.Owner ?? username;
                    core["acl"] = null;
                }
            }

            core["meta"] = meta;
            return await blobService.UpdateAsync(token, core, username);
        }

        private Task UpdatePinnedAsync(string type, IEnumerable<string> tokens, JsonNode isPinned, string username)
        {
            var pinned = new JsonObject();
            foreach (var t in tokens) pinned[t] = isPinned?.DeepClone();
            return UpdateStateAsync(type, "default", new JsonObject { ["userPinned"] = pinned }, username);
        }

        private Task TrackChangeAsync(string msg, JsonBlob blob) => TrackChangeAsync(msg, new JsonObject
        {
            ["name"] = blob.Name,
            ["token"] = blob.Token,
            ["isGlobal"] = blob.Owner == null
        });

        private Task TrackChangeAsync(string msg, JsonObject data) =>
            trackService.TrackAsync(new TrackEntry { Msg = msg, Category = "Views", Data = data });

        private static Exception BulkFailure(string msg, List<Exception> failures)
        {
            return failures.All(f => f is HoistException h && h.IsRoutine)
                ? new RoutineRuntimeException(msg)
                : new HoistException(msg, cause: failures[0]);
        }

        private static JsonObject GetStateFromBlob(JsonBlob blob, string viewInstance)
        {
            var raw = JsonUtils.ParseObjectOrArray(blob?.Value) as JsonObject;
            var ret = new JsonObject
            {
                ["userPinned"] = (raw?["userPinned"] as JsonObject)?.DeepClone() ?? new JsonObject(),
                ["autoSave"] = IsTrue(raw?["autoSave"]) ? raw["autoSave"].DeepClone() : false
            };
            if (raw?["currentView"] is JsonObject currentView && viewInstance != null && currentView.ContainsKey(viewInstance))
            {
                ret["currentView"] = currentView[viewInstance]?.DeepClone();
            }
            return ret;
        }

        /// <summary>Groovy truth for a JSON value.</summary>
        private static bool IsTrue(JsonNode node)
        {
            return node switch
            {
                null => false,
                JsonValue v when v.TryGetValue<bool>(out var b) => b,
                JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
                JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
                JsonObject o => o.Count > 0,
                JsonArray a => a.Count > 0,
                _ => true
            };
        }
    }
}
