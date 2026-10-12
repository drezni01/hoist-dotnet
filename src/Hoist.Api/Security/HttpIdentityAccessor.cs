using Hoist.Core.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Hoist.Api.Security
{
    /// <summary>
    /// The current request's Hoist identity - set by HoistAuthMiddleware, the equivalent of the
    /// thread-local HoistIdentity that hoist-core's HoistFilter installs from the session.
    /// </summary>
    public class HoistIdentity
    {
        public const string ItemKey = "xhIdentity";

        public HoistIdentity(string username, string authUsername)
        {
            Username = username;
            AuthUsername = authUsername;
        }

        /// <summary>The apparent user - differs from AuthUsername while impersonating.</summary>
        public string Username { get; }
        public string AuthUsername { get; }
        public bool IsImpersonating => Username != AuthUsername;
    }

    public class HttpIdentityAccessor : IIdentityAccessor
    {
        private readonly IHttpContextAccessor httpContextAccessor;

        public HttpIdentityAccessor(IHttpContextAccessor httpContextAccessor)
        {
            this.httpContextAccessor = httpContextAccessor;
        }

        public HoistIdentity Identity
        {
            get => httpContextAccessor.HttpContext?.Items[HoistIdentity.ItemKey] as HoistIdentity;
            set
            {
                var ctx = httpContextAccessor.HttpContext;
                if (ctx != null) ctx.Items[HoistIdentity.ItemKey] = value;
            }
        }

        public string Username => Identity?.Username;
        public string AuthUsername => Identity?.AuthUsername;
    }
}
