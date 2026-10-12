using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Hoist.Api.Security
{
    /// <summary>
    /// Replaces the servlet HttpSession attribute 'xhIdentity'. The identity (apparent + auth username)
    /// is kept in an encrypted, HttpOnly cookie, so no server-side session state is needed.
    /// </summary>
    public class HoistSession
    {
        public const string CookieName = "xh-identity";

        private readonly IDataProtector protector;
        private readonly HttpIdentityAccessor accessor;

        public HoistSession(IDataProtectionProvider dataProtection, HttpIdentityAccessor accessor)
        {
            protector = dataProtection.CreateProtector("Hoist.Identity.v1");
            this.accessor = accessor;
        }

        /// <summary>installIdentityFromRequest: reads the cookie into the request's identity (null when absent or invalid).</summary>
        public HoistIdentity Load(HttpContext ctx)
        {
            HoistIdentity identity = null;
            if (ctx.Request.Cookies.TryGetValue(CookieName, out var raw) && !string.IsNullOrEmpty(raw))
            {
                try
                {
                    var parts = protector.Unprotect(raw).Split('\n');
                    if (parts.Length == 2) identity = new HoistIdentity(parts[0], parts[1]);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Cookie from another key ring or tampered with - treat as no session.
                }
            }
            accessor.Identity = identity;
            return identity;
        }

        /// <summary>setIdentity: stores the identity in the cookie and on the current request.</summary>
        public void SetIdentity(HttpContext ctx, string username, string authUsername)
        {
            var identity = new HoistIdentity(username, authUsername);
            ctx.Response.Cookies.Append(CookieName, protector.Protect($"{username}\n{authUsername}"), CookieOptions(ctx));
            accessor.Identity = identity;
        }

        /// <summary>clearIdentity.</summary>
        public void Clear(HttpContext ctx)
        {
            ctx.Response.Cookies.Delete(CookieName, CookieOptions(ctx));
            accessor.Identity = null;
        }

        private static CookieOptions CookieOptions(HttpContext ctx) => new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = string.IsNullOrEmpty(ctx.Request.PathBase) ? "/" : ctx.Request.PathBase.Value
        };
    }
}
