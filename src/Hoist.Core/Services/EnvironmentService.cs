using System.Runtime.InteropServices;
using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hoist.Core.Services
{
    /// <summary>Port of hoist-core environment/EnvironmentService.</summary>
    public class EnvironmentService
    {
        private readonly HoistOptions options;
        private readonly ConfigService configService;
        private readonly AlertBannerService alertBannerService;
        private readonly IdentityService identityService;
        private readonly IDatabaseInfo databaseInfo;
        private readonly ILogger<EnvironmentService> logger;

        public EnvironmentService(
            HoistOptions options,
            ConfigService configService,
            AlertBannerService alertBannerService,
            IdentityService identityService,
            IDatabaseInfo databaseInfo,
            ILogger<EnvironmentService> logger)
        {
            this.options = options;
            this.configService = configService;
            this.alertBannerService = alertBannerService;
            this.identityService = identityService;
            this.databaseInfo = databaseInfo;
            this.logger = logger;
        }

        /// <summary>xhAppTimeZone, falling back to UTC when not a valid zone id.</summary>
        public async Task<TimeZoneInfo> GetAppTimeZoneAsync()
        {
            var id = await configService.GetStringAsync("xhAppTimeZone", "UTC");
            var tz = TimeZones.Find(id);
            if (tz == null)
            {
                logger.LogError("Invalid xhAppTimeZone config: '{Id}' not a valid ZoneId - will fall back to UTC.", id);
                tz = TimeZoneInfo.Utc;
            }
            return tz;
        }

        public TimeZoneInfo ServerTimeZone => TimeZoneInfo.Local;

        public async Task<Dictionary<string, object>> GetEnvironmentAsync()
        {
            var serverTz = ServerTimeZone;
            var appTz = await GetAppTimeZoneAsync();
            var ret = new Dictionary<string, object>
            {
                ["appCode"] = options.AppCode,
                ["appName"] = options.AppName,
                ["appVersion"] = options.AppVersion,
                ["appBuild"] = options.AppBuild,
                ["appEnvironment"] = options.Environment,
                ["grailsVersion"] = null,
                ["hoistCoreVersion"] = HoistOptions.HoistCoreVersion,
                ["javaVersion"] = RuntimeInformation.FrameworkDescription,
                ["serverTimeZone"] = TimeZones.IanaId(serverTz),
                ["serverTimeZoneOffset"] = TimeZones.OffsetMillis(serverTz),
                ["appTimeZone"] = TimeZones.IanaId(appTz),
                ["appTimeZoneOffset"] = TimeZones.OffsetMillis(appTz),
                ["webSocketsEnabled"] = false,
                ["instanceName"] = options.InstanceName,
                ["alertBanner"] = await alertBannerService.GetAlertBannerAsync(),
                ["pollConfig"] = await configService.GetObjectAsync("EnvPollConfig")
            };

            if (await identityService.IsHoistAdminReaderAsync(await identityService.GetAuthUserAsync()))
            {
                ret["databaseConnectionString"] = databaseInfo.ConnectionString;
                ret["databaseUser"] = databaseInfo.User;
                ret["databaseCreateMode"] = databaseInfo.CreateMode;
            }
            return ret;
        }

        public async Task<Dictionary<string, object>> EnvironmentPollAsync()
        {
            return new Dictionary<string, object>
            {
                ["appCode"] = options.AppCode,
                ["appVersion"] = options.AppVersion,
                ["appBuild"] = options.AppBuild,
                ["instanceName"] = options.InstanceName,
                ["alertBanner"] = await alertBannerService.GetAlertBannerAsync(),
                ["pollConfig"] = await configService.GetObjectAsync("EnvPollConfig")
            };
        }
    }
}
