using System.Text.Json.Nodes;
using Hoist.Api.Security;
using Hoist.Core.Exceptions;
using Hoist.Core.Filters;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Core.Utils;
using Microsoft.AspNetCore.Mvc;

namespace Hoist.Api.Controllers.Admin
{
    /// <summary>
    /// Port of admin/RoleAdminController. Class access is HOIST_ROLE_MANAGER; config and list need only
    /// HOIST_ADMIN_READER. Writes also re-check the authenticated (not apparent) user. Directory-group
    /// endpoints are not implemented (404) - directoryGroupsSupported is false.
    /// </summary>
    [AccessRequiresRole(HoistRoles.HOIST_ROLE_MANAGER)]
    public class RoleAdminController : BaseController
    {
        private readonly RoleAdminService roleAdminService;
        private readonly IRoleService roleService;
        private readonly IdentityService identityService;

        public RoleAdminController(RoleAdminService roleAdminService, IRoleService roleService, IdentityService identityService)
        {
            this.roleAdminService = roleAdminService;
            this.roleService = roleService;
            this.identityService = identityService;
        }

        [Route("roleAdmin/config")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN_READER)]
        public IActionResult Config()
        {
            if (roleService is not DefaultRoleService) return RenderJson(new { enabled = false });
            var ret = new Dictionary<string, object> { ["enabled"] = true };
            foreach (var (k, v) in roleAdminService.GetClientConfig()) ret[k] = v;
            return RenderJson(ret);
        }

        [Route("roleAdmin/list")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN_READER)]
        public async Task<IActionResult> List()
        {
            EnsureDefaultRoleService();
            return RenderJson(new { data = await roleAdminService.ListAsync() });
        }

        [Route("roleAdmin/create")]
        public async Task<IActionResult> Create()
        {
            await EnsureHoistRoleManagerAsync();
            var role = await roleAdminService.CreateAsync(await ParseRequestJsonAsync());
            return RenderJson(new { data = RoleAdminService.FormatRole(role) });
        }

        [Route("roleAdmin/update")]
        public async Task<IActionResult> Update()
        {
            await EnsureHoistRoleManagerAsync();
            var role = await roleAdminService.UpdateAsync(await ParseRequestJsonAsync());
            return RenderJson(new { data = RoleAdminService.FormatRole(role) });
        }

        [Route("roleAdmin/delete")]
        public async Task<IActionResult> Delete()
        {
            await EnsureHoistRoleManagerAsync();
            var spec = await ParseRequestJsonAsync();
            await roleAdminService.DeleteAsync(spec["name"]?.ToString());
            return RenderSuccess();
        }

        [Route("roleAdmin/bulkCategoryUpdate")]
        public async Task<IActionResult> BulkCategoryUpdate()
        {
            await EnsureHoistRoleManagerAsync();
            var body = await ParseRequestJsonAsync();
            var roles = (body["roles"] as JsonArray ?? new JsonArray()).Select(r => r?.ToString()).ToList();
            var category = body["category"] == null ? null : JsonUtils.ToStoredString(body["category"]);
            var updated = await roleAdminService.BulkCategoryUpdateAsync(roles, category);
            return RenderJson(new { data = updated.Select(RoleAdminService.FormatRole).ToList() });
        }

        private void EnsureDefaultRoleService()
        {
            if (roleService is not DefaultRoleService)
            {
                throw new HoistException("Action not enabled - the Hoist-provided DefaultRoleService must be used to enable role management via this service");
            }
        }

        private async Task EnsureHoistRoleManagerAsync()
        {
            EnsureDefaultRoleService();
            if (!await identityService.HasRoleAsync(await identityService.GetAuthUserAsync(), HoistRoles.HOIST_ROLE_MANAGER))
            {
                throw new HoistException($"AuthUser {identityService.AuthUsername} is not a 'HOIST_ROLE_MANAGER'");
            }
        }
    }

    /// <summary>Port of admin/UserAdminController.</summary>
    [AccessRequiresRole(HoistRoles.HOIST_ADMIN_READER)]
    public class UserAdminController : BaseController
    {
        private readonly IHoistUserService userService;
        private readonly IRoleService roleService;

        public UserAdminController(IHoistUserService userService, IRoleService roleService)
        {
            this.userService = userService;
            this.roleService = roleService;
        }

        [Route("userAdmin/users")]
        public async Task<IActionResult> Users()
        {
            var raw = Param("activeOnly");
            var activeOnly = raw switch
            {
                "true" => true,
                "false" => false,
                _ => throw new HoistException($"Unable to parse '{raw}' as a strict boolean - must be 'true' or 'false'")
            };
            return RenderJson((await userService.ListAsync(activeOnly)).Select(u => u.FormatForJson()).ToList());
        }

        [Route("userAdmin/roles")]
        public async Task<IActionResult> Roles()
        {
            var all = await roleService.GetAllRoleAssignmentsAsync();
            return RenderJson(all.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(u => u, StringComparer.Ordinal).ToList()));
        }

        [Route("userAdmin/rolesForUser")]
        public async Task<IActionResult> RolesForUser(string user)
        {
            var roles = await roleService.GetRolesForUserAsync(user);
            return RenderJson(new { user, roles = roles.OrderBy(r => r, StringComparer.Ordinal).ToList() });
        }

        [Route("userAdmin/usersForRole")]
        public async Task<IActionResult> UsersForRole(string role)
        {
            var users = await roleService.GetUsersForRoleAsync(role);
            return RenderJson(new { role, users = users.OrderBy(u => u, StringComparer.Ordinal).ToList() });
        }
    }

    /// <summary>Port of admin/TrackLogAdminController - the Admin Console Activity tab.</summary>
    [AccessRequiresRole(HoistRoles.HOIST_ADMIN_READER)]
    public class TrackLogAdminController : BaseController
    {
        private readonly TrackLogAdminService trackLogAdminService;

        public TrackLogAdminController(TrackLogAdminService trackLogAdminService)
        {
            this.trackLogAdminService = trackLogAdminService;
        }

        /// <summary>JSON body {startDay?, endDay?, filters?, maxRows?} - days as yyyy-MM-dd or yyyyMMdd. Returns a bare array.</summary>
        [Route("trackLogAdmin")]
        [Route("trackLogAdmin/index")]
        public async Task<IActionResult> Index()
        {
            var query = await ParseRequestJsonAsync();
            var startDay = ParseDay(query["startDay"]) ?? new DateOnly(1970, 1, 1);
            var endDay = ParseDay(query["endDay"]) ?? await trackLogAdminService.AppDayAsync();
            var filter = HoistFilter.Parse(query["filters"]);
            var maxRows = query["maxRows"] is JsonValue m && m.TryGetValue<int>(out var rows) ? rows : (int?)null;
            return RenderJson(await trackLogAdminService.QueryTrackLogAsync(filter, startDay, endDay, maxRows));
        }

        [Route("trackLogAdmin/lookups")]
        public async Task<IActionResult> Lookups()
        {
            return RenderJson(await trackLogAdminService.LookupsAsync());
        }

        private static DateOnly? ParseDay(JsonNode node)
        {
            var s = node?.ToString();
            if (string.IsNullOrEmpty(s)) return null;
            return DateOnly.ParseExact(s, s.Length == 8 ? "yyyyMMdd" : "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Port of admin/AlertBannerAdminController.</summary>
    [AccessRequiresRole(HoistRoles.HOIST_ADMIN_READER)]
    public class AlertBannerAdminController : BaseController
    {
        private readonly AlertBannerService alertBannerService;

        public AlertBannerAdminController(AlertBannerService alertBannerService)
        {
            this.alertBannerService = alertBannerService;
        }

        [Route("alertBannerAdmin/alertSpec")]
        public async Task<IActionResult> AlertSpec() => RenderJson(await alertBannerService.GetAlertSpecAsync());

        [Route("alertBannerAdmin/alertPresets")]
        public async Task<IActionResult> AlertPresets() => RenderJson(await alertBannerService.GetAlertPresetsAsync());

        [Route("alertBannerAdmin/setAlertSpec")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN)]
        public async Task<IActionResult> SetAlertSpec()
        {
            await alertBannerService.SetAlertSpecAsync(await ParseRequestJsonAsync());
            return RenderSuccess();
        }

        [Route("alertBannerAdmin/setAlertPresets")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN)]
        public async Task<IActionResult> SetAlertPresets()
        {
            await alertBannerService.SetAlertPresetsAsync(await ParseRequestJsonArrayAsync());
            return RenderSuccess();
        }
    }
}
