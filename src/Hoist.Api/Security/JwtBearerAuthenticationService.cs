using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Hoist.Api.Security
{
    /// <summary>Settings of the Jwt auth provider (AddHoistJwtAuth) - typically bound from e.g. "Hoist:Auth".</summary>
    public class HoistJwtOptions
    {
        /// <summary>Base64 HS256 signing key - the same key the token issuer (the host app) signs with. Required.</summary>
        public string SigningKey { get; set; }

        /// <summary>Checked only when set.</summary>
        public string Issuer { get; set; }

        /// <summary>Checked only when set.</summary>
        public string Audience { get; set; }

        /// <summary>JWT claim holding the Hoist username.</summary>
        public string UsernameClaim { get; set; } = "unique_name";
        public string EmailClaim { get; set; } = "email";
        public string DisplayNameClaim { get; set; } = "name";

        /// <summary>Lowercase usernames, as required by HoistUser.validateUsername.</summary>
        public bool LowercaseUsernames { get; set; } = true;

        /// <summary>
        /// JWT claim naming the real user of an impersonation token - a token the host issued for the
        /// target user so its own API acts as them (docs/api/auth.md "Impersonation tokens").
        /// </summary>
        public string ImpersonatorClaim { get; set; } = "impersonator";

        public TokenValidationParameters ToValidationParameters()
        {
            if (string.IsNullOrEmpty(SigningKey))
            {
                throw new HoistException("Hoist Jwt auth needs a SigningKey (Base64 HS256 key), e.g. bound from Hoist:Auth:SigningKey.");
            }
            return new TokenValidationParameters
            {
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(SigningKey)),
                ValidateIssuerSigningKey = true,
                ValidateIssuer = !string.IsNullOrEmpty(Issuer),
                ValidIssuer = Issuer,
                ValidateAudience = !string.IsNullOrEmpty(Audience),
                ValidAudience = Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };
        }
    }

    /// <summary>
    /// The Jwt auth provider: the client sends the JWT its auth flow obtained (e.g. from the host app)
    /// as an Authorization: Bearer header. Mirrors a Grails pass-through AuthenticationService, but validates
    /// the token in-process instead of calling back to the token issuer.
    /// </summary>
    public class JwtBearerAuthenticationService : BaseAuthenticationService
    {
        private readonly HoistJwtOptions options;
        private readonly TokenValidationParameters parameters;
        private readonly IHoistUserService userService;
        private readonly IHoistUserProvisioner provisioner;
        private readonly ILogger<JwtBearerAuthenticationService> logger;
        private readonly JsonWebTokenHandler handler = new JsonWebTokenHandler();

        private readonly IServiceProvider services;

        public JwtBearerAuthenticationService(
            HoistSession session,
            HoistJwtOptions options,
            IHoistUserService userService,
            IServiceProvider services,
            ILogger<JwtBearerAuthenticationService> logger) : base(session)
        {
            this.options = options;
            parameters = options.ToValidationParameters();
            this.userService = userService;
            this.services = services;
            provisioner = services.GetService(typeof(IHoistUserProvisioner)) as IHoistUserProvisioner;
            this.logger = logger;
        }

        public override async Task<bool> CompleteAuthenticationAsync(HttpContext ctx)
        {
            var identity = await IdentityFromBearerAsync(ctx);
            if (identity == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return false;
            }
            SetIdentity(ctx, identity);
            return true;
        }

        /// <summary>The username must match the Bearer token's user - the password is not used.</summary>
        public override async Task<bool> LoginAsync(HttpContext ctx, string username, string password)
        {
            var identity = await IdentityFromBearerAsync(ctx);
            if (identity == null) return false;
            if (!string.Equals(identity.Apparent.Username, Normalize(username), StringComparison.Ordinal))
            {
                logger.LogInformation("Login refused: username {Username} does not match token user {TokenUser}", username, identity.Apparent.Username);
                return false;
            }
            SetIdentity(ctx, identity);
            return true;
        }

        /// <summary>Nothing to revoke - the session is cleared by the caller.</summary>
        public override Task<bool> LogoutAsync(HttpContext ctx) => Task.FromResult(true);

        /// <summary>Who a Bearer token identifies: Apparent is the token's user; Auth differs for an impersonation token.</summary>
        public record BearerIdentity(HoistUser Apparent, HoistUser Auth)
        {
            public bool IsImpersonation => !Apparent.Equals(Auth);
        }

        /// <summary>
        /// The identity for a valid Bearer token; null if absent or invalid. A token with the impersonator
        /// claim (issued by the host for an impersonation) identifies the impersonator browsing as the
        /// token's user, and only while Hoist's impersonation rules still allow it.
        /// </summary>
        public async Task<BearerIdentity> IdentityFromBearerAsync(HttpContext ctx)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

            var result = await handler.ValidateTokenAsync(header.Substring(7).Trim(), parameters);
            if (!result.IsValid)
            {
                logger.LogDebug("Bearer token rejected: {Message}", result.Exception?.Message);
                return null;
            }

            var username = Claim(result, options.UsernameClaim);
            if (string.IsNullOrEmpty(username))
            {
                logger.LogWarning("Bearer token has no '{Claim}' claim", options.UsernameClaim);
                return null;
            }

            var impersonator = Claim(result, options.ImpersonatorClaim);
            if (!string.IsNullOrEmpty(impersonator)) return await ImpersonationIdentityAsync(Normalize(impersonator), Normalize(username));

            var fromToken = new HoistUser(Normalize(username), Claim(result, options.EmailClaim), Claim(result, options.DisplayNameClaim));
            var user = provisioner != null
                ? await provisioner.GetOrCreateAsync(fromToken)
                : await userService.FindAsync(fromToken.Username);
            return user == null ? null : new BearerIdentity(user, user);
        }

        private async Task<BearerIdentity> ImpersonationIdentityAsync(string impersonator, string username)
        {
            var authUser = await userService.FindAsync(impersonator);
            try
            {
                // Resolved lazily: IdentityService is not needed by plain tokens.
                var identityService = (IdentityService)services.GetService(typeof(IdentityService));
                var target = await identityService.CheckImpersonationAsync(authUser, username);
                return new BearerIdentity(target, authUser);
            }
            catch (HoistException e)
            {
                logger.LogWarning("Impersonation token refused: '{Impersonator}' as '{Username}' - {Message}", impersonator, username, e.Message);
                return null;
            }
        }

        private void SetIdentity(HttpContext ctx, BearerIdentity identity)
        {
            if (identity.IsImpersonation) SetImpersonatedUser(ctx, identity.Auth, identity.Apparent);
            else SetUser(ctx, identity.Apparent);
        }

        private string Normalize(string username) => options.LowercaseUsernames ? username?.ToLowerInvariant() : username;

        private static string Claim(TokenValidationResult result, string type)
        {
            if (type == null || !result.Claims.TryGetValue(type, out var value)) return null;
            return value is IEnumerable<object> list && value is not string ? list.FirstOrDefault()?.ToString() : value?.ToString();
        }
    }
}
