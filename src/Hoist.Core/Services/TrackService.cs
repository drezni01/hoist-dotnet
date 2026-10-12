using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hoist.Core.Services
{
    /// <summary>One activity entry, from the client (xh/track) or from server code. Mirrors TrackService.track's parameters.</summary>
    public class TrackEntry
    {
        public string Msg { get; set; }
        public string Category { get; set; }
        public string Severity { get; set; }
        public JsonNode Data { get; set; }
        public JsonNode LogData { get; set; }
        public string CorrelationId { get; set; }
        public long? Timestamp { get; set; }
        public long? Elapsed { get; set; }
        public string Username { get; set; }
        public string Impersonating { get; set; }
        public string ClientAppCode { get; set; }
        public string TabId { get; set; }
        public string LoadId { get; set; }
        public string Url { get; set; }
        public string AppVersion { get; set; }

        public static TrackEntry FromJson(JsonObject o) => new TrackEntry
        {
            Msg = Str(o["msg"]),
            Category = Str(o["category"]),
            Severity = Str(o["severity"]),
            Data = o["data"]?.DeepClone(),
            LogData = o["logData"]?.DeepClone(),
            CorrelationId = Str(o["correlationId"]),
            Timestamp = Long(o["timestamp"]),
            Elapsed = Long(o["elapsed"]),
            Username = Str(o["username"]),
            Impersonating = Str(o["impersonating"]),
            ClientAppCode = Str(o["clientAppCode"]),
            TabId = Str(o["tabId"]),
            LoadId = Str(o["loadId"]),
            Url = Str(o["url"]),
            AppVersion = Str(o["appVersion"])
        };

        private static string Str(JsonNode n) => n == null ? null : JsonUtils.ToStoredString(n);

        private static long? Long(JsonNode n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number) return (long)e.GetDouble();
            return v.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed) ? parsed : null;
        }
    }

    /// <summary>
    /// Port of hoist-core track/TrackService. Entries are normalised on the calling thread (to capture
    /// identity and request headers) and then logged and persisted asynchronously, never failing the
    /// caller. Client errors and feedback arrive here too, as categories 'Client Error' and 'Feedback'.
    /// </summary>
    public class TrackService
    {
        private static readonly string[] Severities = { "DEBUG", "INFO", "WARN", "ERROR" };

        private readonly ITrackLogRepository repo;
        private readonly ConfigService configService;
        private readonly IIdentityAccessor identity;
        private readonly ITrackRequestInfo requestInfo;
        private readonly IInstanceConfig instanceConfig;
        private readonly HoistOptions options;
        private readonly ILogger<TrackService> logger;
        private readonly ILogger trackLogger;
        private readonly RateMonitor rateMonitor = new RateMonitor(TimeSpan.FromMinutes(1));
        private readonly ConcurrentDictionary<Task, bool> pending = new ConcurrentDictionary<Task, bool>();
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);

        private bool rateLimitActive;

        public TrackService(
            ITrackLogRepository repo,
            ConfigService configService,
            IIdentityAccessor identity,
            ITrackRequestInfo requestInfo,
            IInstanceConfig instanceConfig,
            HoistOptions options,
            ILoggerFactory loggerFactory)
        {
            this.repo = repo;
            this.configService = configService;
            this.identity = identity;
            this.requestInfo = requestInfo;
            this.instanceConfig = instanceConfig;
            this.options = options;
            logger = loggerFactory.CreateLogger<TrackService>();
            trackLogger = loggerFactory.CreateLogger("Hoist.TrackLog");
        }

        private bool PersistenceDisabled => instanceConfig.Get("disableTrackLog") == "true";

        public Task TrackAsync(TrackEntry entry) => TrackAllAsync(new[] { entry });

        public async Task TrackAllAsync(IEnumerable<TrackEntry> entries)
        {
            try
            {
                var conf = await GetConfAsync();
                if (conf["enabled"]?.GetValue<bool>() != true)
                {
                    logger.LogTrace("Tracking disabled via config.");
                    return;
                }

                var list = entries.ToList();
                ApplyRateLimiting(list.Count, conf["maxEntriesPerMin"]?.GetValue<long>() ?? 1000);
                var doPersist = !PersistenceDisabled && !rateLimitActive;

                // Normalise on this thread to capture identity and request context.
                var prepared = list.Select(PrepareEntry).ToList();

                var task = Task.Run(() => ProcessAsync(prepared, conf, doPersist));
                pending[task] = true;
                _ = task.ContinueWith(t => pending.TryRemove(t, out _), TaskScheduler.Default);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Exception writing track log");
            }
        }

        /// <summary>Waits for queued entries to be written - for tests and orderly shutdown.</summary>
        public Task FlushAsync() => Task.WhenAll(pending.Keys.ToArray());

        //------------------------
        // Implementation
        //------------------------
        private async Task<JsonObject> GetConfAsync() => await configService.GetObjectAsync("ActivityTrackingConfig");

        private PreparedEntry PrepareEntry(TrackEntry e)
        {
            var isImpersonating = identity.Username != null && identity.Username != identity.AuthUsername;
            return new PreparedEntry
            {
                Username = !string.IsNullOrEmpty(e.Username) ? e.Username : identity.AuthUsername,
                Impersonating = !string.IsNullOrEmpty(e.Impersonating) ? e.Impersonating : (isImpersonating ? identity.Username : null),
                Category = !string.IsNullOrEmpty(e.Category) ? e.Category : "Default",
                CorrelationId = e.CorrelationId,
                Msg = e.Msg,
                Elapsed = e.Elapsed,
                Severity = ParseSeverity(e.Severity),
                Data = e.Data != null && IsTruthy(e.Data) ? JsonUtils.Serialize(e.Data) : null,
                RawData = e.Data,
                LogData = e.LogData,
                Url = e.Url == null ? null : e.Url.Length > TrackLog.MaxUrlLength ? e.Url.Substring(0, TrackLog.MaxUrlLength) : e.Url,
                AppVersion = !string.IsNullOrEmpty(e.AppVersion) ? e.AppVersion : options.AppVersion,
                LoadId = e.LoadId,
                TabId = e.TabId,
                ClientAppCode = e.ClientAppCode,
                DateCreated = e.Timestamp.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp.Value).UtcDateTime : DateTime.UtcNow,
                Instance = options.InstanceName,
                AppEnvironment = options.Environment,
                UserAgent = requestInfo.HasRequest ? requestInfo.UserAgent : null,
                Browser = BrowserUtils.GetBrowser(requestInfo.UserAgent, requestInfo.SecChUa, requestInfo.HasRequest),
                Device = BrowserUtils.GetDevice(requestInfo.UserAgent, requestInfo.SecChUaPlatform, requestInfo.HasRequest)
            };
        }

        private async Task ProcessAsync(List<PreparedEntry> entries, JsonObject conf, bool doPersist)
        {
            // Serialised so that a comment on a client error always sees the error row it follows.
            await writeLock.WaitAsync();
            try
            {
                foreach (var entry in entries)
                {
                    try
                    {
                        LogEntry(entry, conf);
                        if (doPersist && await TryMergeClientErrorCommentAsync(entry)) continue;

                        var log = CreateTrackLog(entry, conf);
                        if (doPersist && IsSeverityActive(log, conf) && IsValid(log))
                        {
                            await repo.SaveAsync(log);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Exception recording track log");
                    }
                }
            }
            finally
            {
                writeLock.Release();
            }
        }

        private async Task<bool> TryMergeClientErrorCommentAsync(PreparedEntry entry)
        {
            if (entry.Category != "Client Error") return false;
            var raw = entry.RawData as JsonObject;
            var userMessage = raw?["userMessage"] is JsonValue um && um.TryGetValue<string>(out var s) ? s : null;
            var errorData = raw?["error"] as JsonObject;
            if (string.IsNullOrEmpty(userMessage) || errorData == null || string.IsNullOrEmpty(entry.LoadId)) return false;

            var candidates = await repo.FindRecentClientErrorsAsync(entry.Username, entry.LoadId, DateTime.UtcNow.AddMinutes(-15), 10);
            foreach (var prior in candidates)
            {
                JsonObject data;
                try
                {
                    data = JsonNode.Parse(prior.Data ?? "null") as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (data == null || IsTruthy(data["userMessage"]) || !JsonNode.DeepEquals(data["error"], errorData)) continue;

                data["userMessage"] = userMessage;
                await repo.UpdateDataAsync(prior.Id, JsonUtils.Serialize(data));
                logger.LogDebug("Merged user comment onto prior client error | trackLogId {Id}", prior.Id);
                return true;
            }
            return false;
        }

        private TrackLog CreateTrackLog(PreparedEntry entry, JsonObject conf)
        {
            var data = entry.Data;
            var maxDataLength = conf["maxDataLength"]?.GetValue<int>() ?? 2000;
            if (data != null && data.Length > maxDataLength)
            {
                logger.LogTrace("Track log with message [{Msg}] includes {Length} chars of JSON data | exceeds limit of {Max} | data will not be persisted", entry.Msg, data.Length, maxDataLength);
                data = null;
            }

            var elapsed = entry.Elapsed;
            var byCategory = conf["maxElapsedMinsByCategory"] as JsonObject;
            var maxElapsedMins = byCategory?[entry.Category] is JsonValue m && m.TryGetValue<int>(out var catMins) && catMins != 0
                ? catMins
                : conf["maxElapsedMins"]?.GetValue<int>() ?? 2;
            if (maxElapsedMins > 0 && elapsed > maxElapsedMins * 60_000L)
            {
                logger.LogTrace("Track log with message [{Msg}] has elapsed of {Elapsed}ms | exceeds limit of {Max}m | elapsed will not be persisted", entry.Msg, elapsed, maxElapsedMins);
                elapsed = null;
            }

            return new TrackLog
            {
                Username = entry.Username,
                Impersonating = entry.Impersonating,
                DateCreated = entry.DateCreated,
                Category = entry.Category,
                Msg = StringUtils.Elide(entry.Msg, TrackLog.MaxMsgLength),
                Data = data,
                Elapsed = elapsed.HasValue ? (int)Math.Min(elapsed.Value, int.MaxValue) : null,
                Severity = entry.Severity,
                CorrelationId = entry.CorrelationId,
                LoadId = entry.LoadId,
                TabId = entry.TabId,
                Instance = entry.Instance,
                ClientAppCode = entry.ClientAppCode,
                Browser = entry.Browser,
                Device = entry.Device,
                UserAgent = entry.UserAgent?.Length > 255 ? entry.UserAgent.Substring(0, 255) : entry.UserAgent,
                AppVersion = entry.AppVersion,
                AppEnvironment = entry.AppEnvironment,
                Url = StringUtils.Elide(entry.Url, TrackLog.MaxUrlLength)
            };
        }

        /// <summary>The first matching levels rule decides the minimum severity persisted.</summary>
        private static bool IsSeverityActive(TrackLog log, JsonObject conf)
        {
            var levels = conf["levels"] as JsonArray ?? new JsonArray();
            var match = levels.OfType<JsonObject>().FirstOrDefault(l =>
            {
                var user = l["username"]?.GetValue<string>() ?? "*";
                var cat = l["category"]?.GetValue<string>() ?? "*";
                return (user == "*" || user.Contains(log.Username ?? "")) && (cat == "*" || cat.Contains(log.Category ?? ""));
            });
            return Rank(ParseSeverity(match?["severity"]?.GetValue<string>())) <= Rank(log.Severity);
        }

        /// <summary>GORM constraints on TrackLog - a row that fails them is logged and dropped, as in hoist-core.</summary>
        private bool IsValid(TrackLog log)
        {
            var errors = new List<string>();
            void Check(string field, string value, int max, bool required = false)
            {
                if (required && string.IsNullOrEmpty(value)) errors.Add($"{field} is required");
                else if (value?.Length > max) errors.Add($"{field} exceeds {max}");
            }
            Check("username", log.Username, 50, required: true);
            Check("impersonating", log.Impersonating, 50);
            Check("category", log.Category, 100, required: true);
            Check("msg", log.Msg, TrackLog.MaxMsgLength, required: true);
            Check("correlationId", log.CorrelationId, 100);
            Check("loadId", log.LoadId, 8);
            Check("tabId", log.TabId, 8);
            Check("instance", log.Instance, 50);
            Check("clientAppCode", log.ClientAppCode, 50);
            Check("browser", log.Browser, 100);
            Check("device", log.Device, 100);
            Check("appVersion", log.AppVersion, 100);
            Check("appEnvironment", log.AppEnvironment, 100);
            if (errors.Count == 0) return true;
            logger.LogError("Exception recording track log | invalid TrackLog: {Errors}", string.Join(", ", errors));
            return false;
        }

        /// <summary>TrackLoggingService: a log line per entry, with selected data keys when logData asks for them.</summary>
        private void LogEntry(PreparedEntry entry, JsonObject conf)
        {
            var parts = new List<string>();
            var name = entry.Impersonating != null ? $"{entry.Username} (as {entry.Impersonating})" : entry.Username;
            if (name != null) parts.Add($"_user={name}");
            parts.Add($"_category={entry.Category}");
            if (entry.Msg != null) parts.Add($"_msg={entry.Msg}");
            if (entry.CorrelationId != null) parts.Add($"_correlationId={entry.CorrelationId}");
            parts.Add($"_timestamp={entry.DateCreated:HH:mm:ss.fff}");
            if (entry.Elapsed != null) parts.Add($"_elapsedMs={entry.Elapsed}");

            if (entry.RawData is JsonObject data)
            {
                var logData = entry.LogData ?? conf["logData"];
                var logAll = logData is JsonValue lv && lv.TryGetValue<bool>(out var b) && b;
                var keys = logData is JsonArray arr ? arr.Select(k => k?.ToString()).ToHashSet() : null;
                if (logAll || keys != null)
                {
                    foreach (var (k, v) in data)
                    {
                        if ((logAll || keys.Contains(k)) && v is not JsonObject && v is not JsonArray) parts.Add($"{k}={v}");
                    }
                }
            }

            var level = entry.Severity switch
            {
                "DEBUG" => LogLevel.Debug,
                "WARN" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => LogLevel.Information
            };
            trackLogger.Log(level, "{Entry}", string.Join(" | ", parts));
        }

        private void ApplyRateLimiting(int count, long maxPerMin)
        {
            rateMonitor.NoteRequests(count, maxPerMin);
            if (!rateLimitActive && rateMonitor.LimitExceeded)
            {
                logger.LogError("Track persistence disabled due to non-compliant load | entriesPerMin {Max}", maxPerMin);
                rateLimitActive = true;
            }
            else if (rateLimitActive && rateMonitor.PeriodsInCompliance >= 2)
            {
                logger.LogInformation("Track persistence being re-enabled after multiple periods of compliant load. | compliantMins {Mins}", rateMonitor.PeriodsInCompliance);
                rateLimitActive = false;
            }
        }

        /// <summary>TrackSeverity.parse: unknown or empty values are INFO.</summary>
        public static string ParseSeverity(string s)
        {
            var upper = s?.Trim().ToUpperInvariant();
            return Severities.Contains(upper) ? upper : "INFO";
        }

        private static int Rank(string severity) => Array.IndexOf(Severities, severity);

        /// <summary>Groovy truth for JSON values: null, false, 0, "" and empty collections are false.</summary>
        private static bool IsTruthy(JsonNode node)
        {
            return node switch
            {
                null => false,
                JsonObject o => o.Count > 0,
                JsonArray a => a.Count > 0,
                JsonValue v when v.TryGetValue<bool>(out var b) => b,
                JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
                JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
                _ => true
            };
        }

        private class PreparedEntry
        {
            public string Username { get; set; }
            public string Impersonating { get; set; }
            public string Category { get; set; }
            public string CorrelationId { get; set; }
            public string Msg { get; set; }
            public long? Elapsed { get; set; }
            public string Severity { get; set; }
            public string Data { get; set; }
            public JsonNode RawData { get; set; }
            public JsonNode LogData { get; set; }
            public string Url { get; set; }
            public string AppVersion { get; set; }
            public string LoadId { get; set; }
            public string TabId { get; set; }
            public string ClientAppCode { get; set; }
            public DateTime DateCreated { get; set; }
            public string Instance { get; set; }
            public string AppEnvironment { get; set; }
            public string UserAgent { get; set; }
            public string Browser { get; set; }
            public string Device { get; set; }
        }
    }

    /// <summary>Port of hoist-core util/RateMonitor: requests per fixed period, and consecutive compliant periods.</summary>
    public class RateMonitor
    {
        private readonly TimeSpan period;
        private readonly object sync = new object();
        private DateTime periodStart = DateTime.UtcNow;
        private long periodRequests;
        private long maxPeriodRequests = long.MaxValue;

        public RateMonitor(TimeSpan period)
        {
            this.period = period;
        }

        public bool LimitExceeded { get; private set; }
        public int PeriodsInCompliance { get; private set; }

        public void NoteRequests(int count, long max)
        {
            lock (sync)
            {
                maxPeriodRequests = max;
                var now = DateTime.UtcNow;
                if (now - periodStart >= period)
                {
                    var elapsedPeriods = (int)((now - periodStart).Ticks / period.Ticks);
                    PeriodsInCompliance = periodRequests <= maxPeriodRequests ? PeriodsInCompliance + elapsedPeriods : Math.Max(0, elapsedPeriods - 1);
                    periodStart = now;
                    periodRequests = 0;
                }
                periodRequests += count;
                LimitExceeded = periodRequests > maxPeriodRequests;
                if (LimitExceeded) PeriodsInCompliance = 0;
            }
        }
    }
}
