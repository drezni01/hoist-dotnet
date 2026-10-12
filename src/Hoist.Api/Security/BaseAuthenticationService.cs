using Hoist.Core.Exceptions;
using Hoist.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Hoist.Api.Security
{
    /// <summary>
    /// Port of hoist-core BaseAuthenticationService's template methods. Host apps implement this
    /// (or subclass BaseAuthenticationService). Hosts pick a built-in provider (AddHoistJwtAuth), which
    /// registers its implementation; a host may instead register its own IHoistAuthenticationService in DI.
    /// </summary>
    public interface IHoistAuthenticationService
    {
        /// <summary>Public, pre-auth config for the client (xh/authConfig). Default: empty object.</summary>
        object GetClientConfig();

        /// <summary>
        /// Called for a non-whitelisted request with no authenticated session. Establish the user
        /// (via SetUser) and return true, or write a response (e.g. 401 / redirect) and return false.
        /// </summary>
        Task<bool> CompleteAuthenticationAsync(HttpContext ctx);

        /// <summary>Interactive login (xh/login). Return true after establishing the user.</summary>
        Task<bool> LoginAsync(HttpContext ctx, string username, string password);

        /// <summary>Return true to clear the Hoist session.</summary>
        Task<bool> LogoutAsync(HttpContext ctx);

        /// <summary>Request paths (matched with EndsWith) that need no authentication.</summary>
        IReadOnlyList<string> WhitelistUris { get; }
    }

    public abstract class BaseAuthenticationService : IHoistAuthenticationService
    {
        private static readonly string[] DefaultWhitelist =
        {
            "/ping", // legacy alias for /xh/ping
            "/xh/login",
            "/xh/logout",
            "/xh/ping",
            "/xh/version",
            "/xh/authConfig"
        };

        protected BaseAuthenticationService(HoistSession session)
        {
            Session = session;
        }

        protected HoistSession Session { get; }

        public virtual IReadOnlyList<string> WhitelistUris => DefaultWhitelist;

        public virtual object GetClientConfig() => new Dictionary<string, object>();

        public abstract Task<bool> CompleteAuthenticationAsync(HttpContext ctx);

        public virtual Task<bool> LoginAsync(HttpContext ctx, string username, string password) => Task.FromResult(false);

        public virtual Task<bool> LogoutAsync(HttpContext ctx) => Task.FromResult(false);

        /// <summary>setUser: records the authenticated user in the session; inactive users are refused.</summary>
        protected void SetUser(HttpContext ctx, HoistUser user)
        {
            if (!user.Active) throw new NotAuthorizedException($"'{user.Username}' is not an active user.");
            Session.SetIdentity(ctx, user.Username, user.Username);
        }

        /// <summary>Records an impersonation in the session: authUser is browsing as apparentUser. Checks are the caller's.</summary>
        protected void SetImpersonatedUser(HttpContext ctx, HoistUser authUser, HoistUser apparentUser)
        {
            if (!authUser.Active) throw new NotAuthorizedException($"'{authUser.Username}' is not an active user.");
            Session.SetIdentity(ctx, apparentUser.Username, authUser.Username);
        }
    }
}
