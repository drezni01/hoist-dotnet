using System.Text.Json.Nodes;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Core.Services
{
    /// <summary>
    /// Port of the request-independent parts of hoist-core user/IdentityService and the role helpers
    /// of the HoistUser trait. The session itself (setIdentity / clearIdentity) lives in Api.
    /// </summary>
    public class IdentityService
    {
        private readonly IIdentityAccessor identity;
        private readonly IHoistUserService userService;
        private readonly IRoleService roleService;
        private readonly ConfigService configService;
        private readonly IServiceProvider services;

        public IdentityService(IIdentityAccessor identity, IHoistUserService userService, IRoleService roleService, ConfigService configService, IServiceProvider services)
        {
            this.identity = identity;
            this.userService = userService;
            this.roleService = roleService;
            this.configService = configService;
            this.services = services;
        }

        public string Username => identity.Username;
        public string AuthUsername => identity.AuthUsername;
        public bool IsImpersonating => identity.Username != null && identity.Username != identity.AuthUsername;

        /// <summary>The apparent user (the impersonated user while impersonating).</summary>
        public Task<HoistUser> GetUserAsync() => FindAsync(identity.Username);

        /// <summary>The user who actually authenticated.</summary>
        public Task<HoistUser> GetAuthUserAsync() => FindAsync(identity.AuthUsername);

        /// <summary>
        /// getClientConfig: null when not authenticated; {user, roles} normally;
        /// {apparentUser, apparentUserRoles, authUser, authUserRoles} while impersonating.
        /// </summary>
        public async Task<Dictionary<string, object>> GetClientConfigAsync()
        {
            var authUser = await GetAuthUserAsync();
            if (authUser == null) return null;
            var apparentUser = await GetUserAsync();

            if (apparentUser != null && !apparentUser.Equals(authUser))
            {
                return new Dictionary<string, object>
                {
                    ["apparentUser"] = apparentUser.FormatForJson(),
                    ["apparentUserRoles"] = await RolesAsync(apparentUser),
                    ["authUser"] = authUser.FormatForJson(),
                    ["authUserRoles"] = await RolesAsync(authUser)
                };
            }
            return new Dictionary<string, object>
            {
                ["user"] = authUser.FormatForJson(),
                ["roles"] = await RolesAsync(authUser)
            };
        }

        //------------------------
        // Impersonation
        //------------------------
        /// <summary>
        /// BaseUserService.impersonationTargetsForUser: none unless the user can impersonate; admins may
        /// target any candidate, other impersonators only non-admins. Candidates come from
        /// IHoistUserService.ImpersonationTargetsAsync (default: all active users); this filter always applies on top.
        /// </summary>
        public async Task<List<HoistUser>> ImpersonationTargetsForUserAsync(HoistUser authUser)
        {
            if (authUser == null || !await CanImpersonateAsync(authUser)) return new List<HoistUser>();
            var isAdmin = await IsHoistAdminAsync(authUser);
            var ret = new List<HoistUser>();
            var candidates = await userService.ImpersonationTargetsAsync(authUser);
            foreach (var target in candidates.Where(t => t != null && t.Active).Distinct())
            {
                if (isAdmin || !await IsHoistAdminAsync(target)) ret.Add(target);
            }
            return ret;
        }

        /// <summary>The checks of IdentityService.impersonate - returns the target user or throws (500, as in hoist-core).</summary>
        public async Task<HoistUser> CheckImpersonationAsync(string username) => await CheckImpersonationAsync(await GetAuthUserAsync(), username);

        /// <summary>
        /// The same checks for an explicit authenticated user, without a Hoist session - e.g. for a host
        /// deciding whether to issue an impersonation token. Returns the target user or throws.
        /// </summary>
        public async Task<HoistUser> CheckImpersonationAsync(HoistUser authUser, string username)
        {
            if (await configService.GetBoolAsync("xhEnableImpersonation") != true)
            {
                throw new HoistException("Impersonation is disabled for this app.");
            }
            if (authUser == null || !await CanImpersonateAsync(authUser))
            {
                throw new HoistException("User not found or user not authorized for impersonation.");
            }
            var target = (await ImpersonationTargetsForUserAsync(authUser)).FirstOrDefault(t => t.Username == username);
            if (target == null)
            {
                if (await userService.FindAsync(username) == null) throw new HoistException($"User '{username}' not found");
                throw new HoistException($"'{authUser}' is not authorized to impersonate '{username}'");
            }
            return target;
        }

        public Task TrackImpersonateAsync(string msg, string target)
        {
            return services.GetRequiredService<TrackService>().TrackAsync(new TrackEntry
            {
                Category = "Impersonate",
                Severity = "WARN",
                Msg = msg,
                Data = new JsonObject { ["target"] = target }
            });
        }

        //------------------------
        // HoistUser role helpers
        //------------------------
        public async Task<List<string>> RolesAsync(HoistUser user) =>
            (await roleService.GetRolesForUserAsync(user.Username)).OrderBy(r => r, StringComparer.Ordinal).ToList();

        public async Task<bool> HasRoleAsync(HoistUser user, string role) => user != null && (await roleService.GetRolesForUserAsync(user.Username)).Contains(role);

        public async Task<bool> HasAnyRoleAsync(HoistUser user, IEnumerable<string> roles)
        {
            if (user == null) return false;
            var held = await roleService.GetRolesForUserAsync(user.Username);
            return roles.Any(held.Contains);
        }

        public async Task<bool> HasAllRolesAsync(HoistUser user, IEnumerable<string> roles)
        {
            if (user == null) return false;
            var held = await roleService.GetRolesForUserAsync(user.Username);
            return roles.All(held.Contains);
        }

        public Task<bool> IsHoistAdminAsync(HoistUser user) => HasRoleAsync(user, HoistRoles.HOIST_ADMIN);
        public Task<bool> IsHoistAdminReaderAsync(HoistUser user) => HasRoleAsync(user, HoistRoles.HOIST_ADMIN_READER);
        public Task<bool> CanImpersonateAsync(HoistUser user) => HasRoleAsync(user, HoistRoles.HOIST_IMPERSONATOR);

        /// <summary>hasGate: the gate config (a string list) contains '*' or the username.</summary>
        public async Task<bool> HasGateAsync(HoistUser user, string gate)
        {
            var list = await configService.GetStringListAsync(gate);
            return list.Contains("*") || list.Contains(user.Username);
        }

        private Task<HoistUser> FindAsync(string username) => string.IsNullOrEmpty(username) ? Task.FromResult<HoistUser>(null) : userService.FindAsync(username);
    }
}
