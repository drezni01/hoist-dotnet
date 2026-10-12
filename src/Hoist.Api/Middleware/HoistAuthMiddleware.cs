using Hoist.Api.Security;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Hoist.Api.Middleware
{
    /// <summary>
    /// Port of hoist-core HoistFilter + BaseAuthenticationService.allowRequest. Loads the identity from
    /// the session, lets authenticated or whitelisted requests through, otherwise asks the
    /// authentication service to complete authentication. Auth failures set only a status code -
    /// no body is rendered to an unverified client.
    /// </summary>
    public class HoistAuthMiddleware
    {
        /// <summary>HttpContext.Items key set when this middleware has run - checked by HoistAccessFilter.</summary>
        public const string RanKey = "Hoist.AuthMiddlewareRan";

        private readonly RequestDelegate next;
        private readonly ILogger<HoistAuthMiddleware> logger;

        public HoistAuthMiddleware(RequestDelegate next, ILogger<HoistAuthMiddleware> logger)
        {
            this.next = next;
            this.logger = logger;
        }

        public async Task InvokeAsync(HttpContext ctx, HoistSession session, IHoistAuthenticationService authService, IHoistUserService userService, HttpIdentityAccessor accessor, HoistBootstrapper bootstrapper)
        {
            if (!HoistExceptionMiddleware.IsHoistEndpoint(ctx))
            {
                await next(ctx);
                return;
            }
            if (!bootstrapper.IsInitialized)
            {
                throw new HoistException("Hoist is not initialized yet - it initializes as the host starts (InitializeHoistAsync).");
            }
            ctx.Items[RanKey] = true;

            bool allow;
            try
            {
                allow = await AllowRequestAsync(ctx, session, authService, userService, accessor);
            }
            catch (Exception e)
            {
                if (HoistExceptionMiddleware.IsRoutine(e)) logger.LogDebug(e, "{Message}", e.Message);
                else logger.LogError(e, "{Message}", e.Message);
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = HoistExceptionMiddleware.GetHttpStatus(e);
                return;
            }

            if (allow) await next(ctx);
        }

        private async Task<bool> AllowRequestAsync(HttpContext ctx, HoistSession session, IHoistAuthenticationService authService, IHoistUserService userService, HttpIdentityAccessor accessor)
        {
            var identity = session.Load(ctx);
            if (identity != null && await userService.FindAsync(identity.AuthUsername) != null) return true;

            // A session whose user is no longer known is dropped, as if absent.
            if (identity != null) accessor.Identity = null;

            if (IsWhitelist(ctx, authService)) return true;

            if (!await authService.CompleteAuthenticationAsync(ctx))
            {
                if (!ctx.Response.HasStarted && ctx.Response.StatusCode == StatusCodes.Status200OK)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                return false;
            }

            if (accessor.AuthUsername == null || await userService.FindAsync(accessor.AuthUsername) == null)
            {
                throw new NotAuthenticatedException();
            }
            return true;
        }

        private static bool IsWhitelist(HttpContext ctx, IHoistAuthenticationService authService)
        {
            var path = ctx.Request.Path.Value ?? "";
            return authService.WhitelistUris.Any(u => path.EndsWith(u, StringComparison.Ordinal));
        }
    }
}
