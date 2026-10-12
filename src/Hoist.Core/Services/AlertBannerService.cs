using System.Text.Json.Nodes;
using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Utils;

namespace Hoist.Core.Services
{
    /// <summary>
    /// Port of hoist-core alertbanner/AlertBannerService. The banner spec is a JsonBlob (type
    /// xhAlertBanner, owner xhAlertBannerService) per environment. The published banner is the spec
    /// when xhAlertBannerConfig.enabled, spec.active and not expired - otherwise {active: false}.
    /// It is re-read every 2 minutes (hoist-core: a timer) and immediately after admin changes.
    /// </summary>
    public class AlertBannerService
    {
        public const string BlobType = "xhAlertBanner";
        public const string BlobOwner = "xhAlertBannerService";
        public const string PresetsBlobName = "xhAlertBannerPresets";
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

        private readonly IJsonBlobRepository blobs;
        private readonly ConfigService configService;
        private readonly HoistOptions options;

        private JsonObject alertBanner;
        private DateTime lastRead = DateTime.MinValue;

        public AlertBannerService(IJsonBlobRepository blobs, ConfigService configService, HoistOptions options)
        {
            this.blobs = blobs;
            this.configService = configService;
            this.options = options;
        }

        public string BlobName => options.IsProduction ? "xhAlertBanner" : $"xhAlertBanner_{options.Environment}";

        public static JsonObject EmptyAlert => new JsonObject { ["active"] = false };

        /// <summary>The banner as published to clients via xh/environment and xh/environmentPoll.</summary>
        public async Task<JsonObject> GetAlertBannerAsync()
        {
            if (alertBanner == null || DateTime.UtcNow - lastRead > RefreshInterval) await RefreshAsync();
            return (JsonObject)(alertBanner ?? EmptyAlert).DeepClone();
        }

        /// <summary>readFromSpec.</summary>
        public async Task RefreshAsync()
        {
            var newSpec = EmptyAlert;
            var config = await configService.GetObjectAsync("AlertBannerConfig");
            if (config["enabled"]?.GetValue<bool>() == true)
            {
                var spec = await GetAlertSpecAsync();
                var active = spec["active"] is JsonValue a && a.TryGetValue<bool>(out var isActive) && isActive;
                var expires = spec["expires"] is JsonValue e && e.TryGetValue<long>(out var ms) ? ms : (long?)null;
                if (active && (expires == null || expires > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                {
                    newSpec = spec;
                }
            }
            alertBanner = newSpec;
            lastRead = DateTime.UtcNow;
        }

        //------------------------
        // Admin entry points
        //------------------------
        public async Task<JsonObject> GetAlertSpecAsync()
        {
            var blob = await FindBlobAsync(BlobName);
            return blob != null ? JsonUtils.ParseObjectOrArray(blob.Value) as JsonObject ?? EmptyAlert : EmptyAlert;
        }

        public async Task<JsonArray> GetAlertPresetsAsync()
        {
            var blob = await FindBlobAsync(PresetsBlobName);
            return blob != null ? JsonUtils.ParseObjectOrArray(blob.Value) as JsonArray ?? new JsonArray() : new JsonArray();
        }

        public async Task SetAlertSpecAsync(JsonObject value)
        {
            await WriteBlobAsync(BlobName, value);
            await RefreshAsync();
        }

        public Task SetAlertPresetsAsync(JsonArray presets) => WriteBlobAsync(PresetsBlobName, presets);

        private async Task<JsonBlob> FindBlobAsync(string name)
        {
            var list = await blobs.ListActiveAsync(BlobType);
            return list.FirstOrDefault(b => b.Name == name && (b.Owner == BlobOwner || b.Acl == "*"));
        }

        private async Task WriteBlobAsync(string name, JsonNode value)
        {
            var blob = await FindBlobAsync(name) ?? new JsonBlob { Type = BlobType, Name = name, Owner = BlobOwner };
            blob.Value = JsonUtils.Serialize(value);
            blob.LastUpdatedBy = BlobOwner;
            await blobs.SaveAsync(blob);
        }
    }
}
