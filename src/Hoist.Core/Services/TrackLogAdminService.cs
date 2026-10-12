using Hoist.Core.Exceptions;
using Hoist.Core.Filters;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;

namespace Hoist.Core.Services
{
    /// <summary>Port of hoist-core track/TrackLogAdminService - the Admin Console's Activity tab.</summary>
    public class TrackLogAdminService
    {
        private readonly ITrackLogRepository repo;
        private readonly ConfigService configService;
        private readonly EnvironmentService environmentService;

        public TrackLogAdminService(ITrackLogRepository repo, ConfigService configService, EnvironmentService environmentService)
        {
            this.repo = repo;
            this.configService = configService;
            this.environmentService = environmentService;
        }

        /// <summary>Rows between the app-timezone start of startDay and end of endDay, newest first.</summary>
        public async Task<List<Dictionary<string, object>>> QueryTrackLogAsync(HoistFilter filter, DateOnly startDay, DateOnly endDay, int? maxRows)
        {
            var conf = await configService.GetObjectAsync("ActivityTrackingConfig");
            if (conf["enabled"]?.GetValue<bool>() != true) throw new DataNotAvailableException("TrackService not available.");

            var maxDefault = conf["maxRows"]?["default"]?.GetValue<int>() ?? 10000;
            var maxLimit = conf["maxRows"]?["limit"]?.GetValue<int>() ?? 25000;
            var rows = Math.Min(maxRows is > 0 ? maxRows.Value : maxDefault, maxLimit);

            var tz = await environmentService.GetAppTimeZoneAsync();
            var start = TimeZoneInfo.ConvertTimeToUtc(startDay.ToDateTime(TimeOnly.MinValue), tz);
            var end = TimeZoneInfo.ConvertTimeToUtc(endDay.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

            var logs = await repo.QueryAsync(start, end, filter?.ToExpression<TrackLog>(), rows);
            return logs.Select(l => FormatForJson(l, tz)).ToList();
        }

        public async Task<Dictionary<string, object>> LookupsAsync()
        {
            return new Dictionary<string, object>
            {
                ["appVersion"] = await repo.DistinctValuesAsync(t => t.AppVersion),
                ["browser"] = await repo.DistinctValuesAsync(t => t.Browser),
                ["category"] = await repo.DistinctValuesAsync(t => t.Category),
                ["device"] = await repo.DistinctValuesAsync(t => t.Device),
                ["username"] = await repo.DistinctValuesAsync(t => t.Username)
            };
        }

        /// <summary>The app's current business day (DateTimeUtils.appDay).</summary>
        public async Task<DateOnly> AppDayAsync()
        {
            var tz = await environmentService.GetAppTimeZoneAsync();
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        }

        /// <summary>TrackLog.formatForJSON - data stays a raw JSON string; day is the app-timezone date.</summary>
        public static Dictionary<string, object> FormatForJson(TrackLog t, TimeZoneInfo appTz) => new Dictionary<string, object>
        {
            ["id"] = t.Id,
            ["username"] = t.Username,
            ["impersonating"] = t.Impersonating,
            ["dateCreated"] = t.DateCreated,
            ["day"] = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t.DateCreated, DateTimeKind.Utc), appTz)),
            ["category"] = t.Category,
            ["msg"] = t.Msg,
            ["data"] = t.Data,
            ["elapsed"] = t.Elapsed,
            ["severity"] = t.Severity,
            ["correlationId"] = t.CorrelationId,
            ["loadId"] = t.LoadId,
            ["tabId"] = t.TabId,
            ["instance"] = t.Instance,
            ["clientAppCode"] = t.ClientAppCode,
            ["browser"] = t.Browser,
            ["device"] = t.Device,
            ["userAgent"] = t.UserAgent,
            ["appVersion"] = t.AppVersion,
            ["appEnvironment"] = t.AppEnvironment,
            ["url"] = t.Url
        };
    }
}
