using System.Text.Json.Nodes;
using Hoist.Api.Security;
using Hoist.Core.Configuration;
using Hoist.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Hoist.Api.Controllers
{
    /// <summary>
    /// Port of hoist-core impl/XhController - the endpoints hoist-react calls during startup and use.
    /// See docs/api/core.md and the per-area docs for contracts.
    /// </summary>
    [AccessAll]
    public class XhController : BaseController
    {
        private readonly HoistOptions options;
        private readonly ConfigService configService;
        private readonly IdentityService identityService;
        private readonly IHoistAuthenticationService authService;
        private readonly HoistSession session;
        private readonly PrefService prefService;
        private readonly EnvironmentService environmentService;
        private readonly TrackService trackService;
        private readonly JsonBlobService jsonBlobService;
        private readonly ViewService viewService;

        public XhController(
            HoistOptions options,
            ConfigService configService,
            IdentityService identityService,
            IHoistAuthenticationService authService,
            HoistSession session,
            PrefService prefService,
            EnvironmentService environmentService,
            TrackService trackService,
            JsonBlobService jsonBlobService,
            ViewService viewService)
        {
            this.options = options;
            this.configService = configService;
            this.identityService = identityService;
            this.authService = authService;
            this.session = session;
            this.prefService = prefService;
            this.environmentService = environmentService;
            this.trackService = trackService;
            this.jsonBlobService = jsonBlobService;
            this.viewService = viewService;
        }

        //------------------------
        // Authentication (docs/api/auth.md)
        //------------------------
        [Route("xh/authConfig")]
        public IActionResult AuthConfig()
        {
            return RenderJson(authService.GetClientConfig());
        }

        [Route("xh/authStatus")]
        public async Task<IActionResult> AuthStatus()
        {
            var identity = await identityService.GetClientConfigAsync();
            return RenderJson(new { authenticated = identity != null, identity });
        }

        [Route("xh/getIdentity")]
        public async Task<IActionResult> GetIdentity()
        {
            return RenderJson(await identityService.GetClientConfigAsync());
        }

        [Route("xh/login")]
        public async Task<IActionResult> Login(string username, string password)
        {
            var success = await authService.LoginAsync(HttpContext, username, password);
            var identity = await identityService.GetClientConfigAsync();
            return RenderJson(new { success, identity });
        }

        [Route("xh/logout")]
        public async Task<IActionResult> Logout()
        {
            var success = await authService.LogoutAsync(HttpContext);
            if (success) session.Clear(HttpContext);
            return RenderJson(new { success });
        }

        //------------------------
        // Impersonation (docs/api/auth.md) - protected internally by IdentityService.
        //------------------------
        [Route("xh/impersonationTargets")]
        public async Task<IActionResult> ImpersonationTargets()
        {
            var targets = await identityService.ImpersonationTargetsForUserAsync(await identityService.GetAuthUserAsync());
            return RenderJson(targets.Select(t => new { username = t.Username }).ToList());
        }

        [Route("xh/impersonate")]
        public async Task<IActionResult> Impersonate(string username)
        {
            var target = await identityService.CheckImpersonationAsync(username);
            var authUsername = identityService.AuthUsername;

            // End any current impersonation first - important for tracking.
            if (identityService.IsImpersonating) await EndImpersonationInternalAsync();

            session.SetIdentity(HttpContext, target.Username, authUsername);
            await identityService.TrackImpersonateAsync("Started impersonation", target.Username);
            return RenderSuccess();
        }

        [Route("xh/endImpersonate")]
        public async Task<IActionResult> EndImpersonate()
        {
            await EndImpersonationInternalAsync();
            return RenderSuccess();
        }

        private async Task EndImpersonationInternalAsync()
        {
            var apparent = identityService.Username;
            var auth = identityService.AuthUsername;
            if (apparent != null && apparent != auth)
            {
                await identityService.TrackImpersonateAsync("Stopped impersonation", apparent);
                session.SetIdentity(HttpContext, auth, auth);
            }
        }

        //------------------------
        // Config (docs/api/config.md)
        //------------------------
        [Route("xh/getConfig")]
        public async Task<IActionResult> GetConfig()
        {
            return RenderJson(await configService.GetClientConfigAsync());
        }

        //------------------------
        // Preferences (docs/api/prefs.md)
        //------------------------
        [Route("xh/getPrefs")]
        public async Task<IActionResult> GetPrefs()
        {
            EnsureClientUsernameMatchesSession(identityService.Username);
            return RenderJson(await prefService.GetClientConfigAsync());
        }

        [Route("xh/setPrefs")]
        public async Task<IActionResult> SetPrefs()
        {
            EnsureClientUsernameMatchesSession(identityService.Username);
            var prefs = await ParseRequestJsonAsync();
            foreach (var (key, value) in prefs)
            {
                switch (value)
                {
                    case JsonObject map:
                        await prefService.SetMapAsync(key, map);
                        break;
                    case JsonArray list:
                        await prefService.SetListAsync(key, list);
                        break;
                    default:
                        await prefService.SetPreferenceAsync(key, Hoist.Core.Utils.JsonUtils.ToStoredString(value));
                        break;
                }
            }
            return RenderJson(new { preferences = await prefService.GetLimitedClientConfigAsync(prefs.Select(kv => kv.Key)) });
        }

        [Route("xh/unsetPrefs")]
        public async Task<IActionResult> UnsetPrefs()
        {
            EnsureClientUsernameMatchesSession(identityService.Username);
            var keys = (await ParseRequestJsonArrayAsync()).Select(k => Hoist.Core.Utils.JsonUtils.ToStoredString(k)).ToList();
            foreach (var key in keys) await prefService.UnsetPreferenceAsync(key);
            return RenderJson(new { preferences = await prefService.GetLimitedClientConfigAsync(keys) });
        }

        [Route("xh/clearUserState")]
        public async Task<IActionResult> ClearUserState()
        {
            EnsureClientUsernameMatchesSession(identityService.Username);
            // hoist-core clears both in one new transaction; here they are two short deletes.
            await prefService.ClearPreferencesAsync();
            await viewService.ClearAllStateAsync();
            return RenderSuccess();
        }

        //------------------------
        // Tracking (docs/api/tracking.md)
        //------------------------
        [Route("xh/track")]
        public async Task<IActionResult> Track()
        {
            EnsureClientUsernameMatchesSession(identityService.Username);
            var payload = await ParseRequestJsonAsync(safeEncode: true);
            var entries = (payload["entries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(TrackEntry.FromJson);
            await trackService.TrackAllAsync(entries);
            return RenderSuccess();
        }

        /// <summary>Client metrics are out of scope - accepted and discarded.</summary>
        [Route("xh/recordMetrics")]
        public IActionResult RecordMetrics()
        {
            EnsureClientUsernameMatchesSession(identityService.Username);
            return RenderSuccess();
        }

        /// <summary>Client trace spans are out of scope - accepted and discarded.</summary>
        [Route("xh/submitSpans")]
        public IActionResult SubmitSpans()
        {
            EnsureClientUsernameMatchesSession(identityService.Username);
            return RenderSuccess();
        }

        //------------------------
        // JsonBlobs (docs/api/jsonblob.md)
        //------------------------
        [Route("xh/getJsonBlob")]
        public async Task<IActionResult> GetJsonBlob(string token)
        {
            return RenderJson(JsonBlobService.FormatForClient(await jsonBlobService.GetAsync(token)));
        }

        [Route("xh/findJsonBlob")]
        public async Task<IActionResult> FindJsonBlob(string type, string name, string owner)
        {
            var ret = await jsonBlobService.FindAsync(type, name, owner);
            return RenderJson(ret == null ? null : JsonBlobService.FormatForClient(ret));
        }

        [Route("xh/listJsonBlobs")]
        public async Task<IActionResult> ListJsonBlobs(string type, bool includeValue)
        {
            var ret = await jsonBlobService.ListAsync(type);
            return RenderJson(ret.Select(b => JsonBlobService.FormatForClient(b, includeValue)).ToList());
        }

        [Route("xh/createJsonBlob")]
        public async Task<IActionResult> CreateJsonBlob(string data)
        {
            var ret = await jsonBlobService.CreateAsync(ParseObjectParam(data));
            return RenderJson(JsonBlobService.FormatForClient(ret));
        }

        [Route("xh/updateJsonBlob")]
        public async Task<IActionResult> UpdateJsonBlob(string token, string update)
        {
            var ret = await jsonBlobService.UpdateAsync(token, ParseObjectParam(update));
            return RenderJson(JsonBlobService.FormatForClient(ret));
        }

        [Route("xh/createOrUpdateJsonBlob")]
        public async Task<IActionResult> CreateOrUpdateJsonBlob(string type, string name, string update)
        {
            var ret = await jsonBlobService.CreateOrUpdateAsync(type, name, ParseObjectParam(update));
            return RenderJson(JsonBlobService.FormatForClient(ret));
        }

        [Route("xh/archiveJsonBlob")]
        public async Task<IActionResult> ArchiveJsonBlob(string token)
        {
            return RenderJson(JsonBlobService.FormatForClient(await jsonBlobService.ArchiveAsync(token)));
        }

        //------------------------
        // Environment (docs/api/environment.md)
        //------------------------
        [Route("xh/environment")]
        public async Task<IActionResult> Environment()
        {
            return RenderJson(await environmentService.GetEnvironmentAsync());
        }

        [Route("xh/environmentPoll")]
        public async Task<IActionResult> EnvironmentPoll()
        {
            return RenderJson(await environmentService.EnvironmentPollAsync());
        }

        //------------------------
        // Misc (docs/api/core.md)
        //------------------------
        [Route("xh/getTimeZoneOffset")]
        public IActionResult GetTimeZoneOffset(string timeZoneId)
        {
            var tz = Hoist.Core.Utils.TimeZones.Find(timeZoneId) ?? throw new Hoist.Core.Exceptions.NotFoundException("Unknown timeZoneId");
            return RenderJson(new { offset = Hoist.Core.Utils.TimeZones.OffsetMillis(tz) });
        }

        [Route("xh/echoHeaders")]
        public IActionResult EchoHeaders()
        {
            return RenderJson(Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()));
        }

        //------------------------
        // Core (docs/api/core.md)
        //------------------------
        [Route("xh/ping")]
        [Route("ping")]
        public IActionResult Ping()
        {
            return RenderJson(new
            {
                appCode = options.AppCode,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                success = true
            });
        }

        [Route("xh/version")]
        public IActionResult Version()
        {
            return RenderJson(new
            {
                appCode = options.AppCode,
                appVersion = options.AppVersion,
                appBuild = options.AppBuild
            });
        }
    }
}
