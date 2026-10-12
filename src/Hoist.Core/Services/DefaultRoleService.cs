using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.Extensions.Logging;

namespace Hoist.Core.Services
{
    /// <summary>hoist-core role/provided/RoleSpec.</summary>
    public class RoleSpec
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Notes { get; set; }
        public List<string> Users { get; set; } = new List<string>();
        public List<string> DirectoryGroups { get; set; } = new List<string>();
        public List<string> Roles { get; set; } = new List<string>();
    }

    /// <summary>
    /// Port of hoist-core DefaultRoleService (read side) over xh_role / xh_role_member.
    /// A ROLE-type member X on role R means "holders of X also get R", resolved breadth-first.
    /// Assignments are cached and recomputed every xhRoleModuleConfig.refreshIntervalSecs, and
    /// immediately after any change made through this server. Directory groups are not supported.
    /// </summary>
    public class DefaultRoleService : IRoleService, IHoistInitializer
    {
        private static readonly string[] BootstrapAdminRoles = { HoistRoles.HOIST_ADMIN, HoistRoles.HOIST_ADMIN_READER, HoistRoles.HOIST_ROLE_MANAGER };

        private readonly IRoleRepository repo;
        private readonly ConfigService configService;
        private readonly HoistOptions options;
        private readonly ILogger<DefaultRoleService> logger;
        private readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);
        private readonly string bootstrapAdminUser;

        private Dictionary<string, HashSet<string>> assignments;
        private DateTime lastRefresh = DateTime.MinValue;
        private int refreshIntervalSecs = 300;

        public DefaultRoleService(IRoleRepository repo, ConfigService configService, HoistOptions options, ILogger<DefaultRoleService> logger)
        {
            this.repo = repo;
            this.configService = configService;
            this.options = options;
            this.logger = logger;
            if (!options.IsProduction && !string.IsNullOrEmpty(options.BootstrapAdminUser))
            {
                bootstrapAdminUser = options.BootstrapAdminUser.ToLowerInvariant();
            }
        }

        public int Order => 10;

        public bool UserAssignmentSupported => true;
        public bool DirectoryGroupsSupported => false;
        public string DirectoryGroupsDescription => null;

        public async Task InitAsync()
        {
            await configService.EnsureRequiredConfigsCreatedAsync(new[]
            {
                new ConfigSpec
                {
                    Name = "xhRoleModuleConfig",
                    ValueType = "json",
                    DefaultValue = System.Text.Json.Nodes.JsonNode.Parse("""{"refreshIntervalSecs":300,"directoryGroupProvider":"auto"}"""),
                    GroupName = "xh.io",
                    Note = "Configures built-in role management via `DefaultRoleService`, including how often the role membership cache refreshes and which directory service resolves directory groups."
                }
            });
            await EnsureRequiredRolesCreatedAsync(new List<RoleSpec>
            {
                new RoleSpec { Name = HoistRoles.HOIST_ADMIN, Category = "Hoist", Notes = "Hoist Admins have full access to all Hoist Admin tools and functionality." },
                new RoleSpec { Name = HoistRoles.HOIST_ADMIN_READER, Category = "Hoist", Notes = "Hoist Admin Readers have read-only access to all Hoist Admin tools and functionality.", Roles = { HoistRoles.HOIST_ADMIN } },
                new RoleSpec { Name = HoistRoles.HOIST_IMPERSONATOR, Category = "Hoist", Notes = "Hoist Impersonators can impersonate other users.", Roles = { HoistRoles.HOIST_ADMIN } },
                new RoleSpec { Name = HoistRoles.HOIST_ROLE_MANAGER, Category = "Hoist", Notes = "Hoist Role Managers can manage roles and their memberships." }
            });
            if (bootstrapAdminUser != null)
            {
                logger.LogInformation("{User} configured as local development bootstrapAdminUser - will be granted {Roles}", bootstrapAdminUser, string.Join(", ", BootstrapAdminRoles));
            }
            await RefreshAsync();
        }

        //------------------------
        // IRoleService
        //------------------------
        public async Task<Dictionary<string, HashSet<string>>> GetAllRoleAssignmentsAsync()
        {
            await EnsureFreshAsync();
            return assignments;
        }

        public async Task<HashSet<string>> GetRolesForUserAsync(string username)
        {
            var ret = new HashSet<string>();
            if (string.IsNullOrEmpty(username)) return ret;
            username = username.ToLowerInvariant();

            foreach (var (role, users) in await GetAllRoleAssignmentsAsync())
            {
                if (users.Contains(username)) ret.Add(role);
            }
            if (bootstrapAdminUser == username) ret.UnionWith(BootstrapAdminRoles);
            return ret;
        }

        public async Task<HashSet<string>> GetUsersForRoleAsync(string role)
        {
            var all = await GetAllRoleAssignmentsAsync();
            return all.TryGetValue(role, out var users) ? users : new HashSet<string>();
        }

        //------------------------
        // Writes used by bootstrap
        //------------------------
        public async Task EnsureRequiredRolesCreatedAsync(List<RoleSpec> specs)
        {
            var current = await repo.ListAsync();
            var created = 0;
            foreach (var spec in specs)
            {
                if (current.Any(r => r.Name == spec.Name)) continue;

                var role = new Role { Name = spec.Name, Category = spec.Category, Notes = spec.Notes, LastUpdatedBy = "defaultRoleService" };
                role.Members.AddRange(spec.Users.Select(u => NewMember(RoleMemberType.USER, u, "defaultRoleService")));
                role.Members.AddRange(spec.DirectoryGroups.Select(g => NewMember(RoleMemberType.DIRECTORY_GROUP, g, "defaultRoleService")));
                role.Members.AddRange(spec.Roles.Select(r => NewMember(RoleMemberType.ROLE, r, "defaultRoleService")));
                await repo.SaveAsync(role);

                logger.LogWarning("Required role {Name} missing and created with default value | verify default is appropriate for this application", spec.Name);
                created++;
            }
            logger.LogDebug("Validated presence of {Count} required roles | created {Created}", specs.Count, created);
            if (created > 0) await RefreshAsync();
        }

        /// <summary>Adds the user as a USER member of the role, if they do not already hold it.</summary>
        public async Task AssignRoleAsync(string username, string roleName)
        {
            if ((await GetRolesForUserAsync(username)).Contains(roleName)) return;
            var role = await repo.FindAsync(roleName);
            if (role == null)
            {
                logger.LogWarning("Failed to find role {Role} to assign to {User} | role will not be assigned", roleName, username);
                return;
            }
            role.Members.Add(NewMember(RoleMemberType.USER, username, "defaultRoleUpdateService"));
            await repo.SaveAsync(role);
            await RefreshAsync();
        }

        public static RoleMember NewMember(RoleMemberType type, string name, string createdBy) => new RoleMember
        {
            Type = type,
            Name = type == RoleMemberType.USER ? name?.ToLowerInvariant() : name,
            CreatedBy = createdBy
        };

        //------------------------
        // Implementation
        //------------------------
        public async Task RefreshAsync()
        {
            await refreshLock.WaitAsync();
            try
            {
                var roles = await repo.ListAsync();
                assignments = GenerateRoleAssignments(roles);
                refreshIntervalSecs = await RefreshIntervalSecsAsync();
                lastRefresh = DateTime.UtcNow;
            }
            finally
            {
                refreshLock.Release();
            }
        }

        /// <summary>For each role: the users of the role and of every role reachable through its ROLE members.</summary>
        public Dictionary<string, HashSet<string>> GenerateRoleAssignments(List<Role> roles)
        {
            var byName = roles.ToDictionary(r => r.Name);
            return roles.ToDictionary(r => r.Name, r =>
            {
                var users = new HashSet<string>();
                foreach (var effective in GetEffectiveRoles(r, byName)) users.UnionWith(effective.Users);
                return users;
            });
        }

        /// <summary>The role plus all roles whose holders also get it (breadth-first through ROLE members).</summary>
        public List<Role> GetEffectiveRoles(Role role, Dictionary<string, Role> byName)
        {
            var ret = new List<Role> { role };
            var visited = new HashSet<string> { role.Name };
            var toVisit = new Queue<Role>(new[] { role });
            while (toVisit.Count > 0)
            {
                var current = toVisit.Dequeue();
                foreach (var name in current.Roles)
                {
                    if (!visited.Add(name)) continue;
                    if (byName.TryGetValue(name, out var effective))
                    {
                        toVisit.Enqueue(effective);
                        ret.Add(effective);
                    }
                    else
                    {
                        logger.LogWarning("Role {Role} references non-existent role {Missing} | skipping", current.Name, name);
                    }
                }
            }
            return ret;
        }

        private async Task EnsureFreshAsync()
        {
            if (assignments != null && DateTime.UtcNow - lastRefresh < TimeSpan.FromSeconds(refreshIntervalSecs)) return;
            await RefreshAsync();
        }

        private async Task<int> RefreshIntervalSecsAsync()
        {
            try
            {
                var conf = await configService.GetMapAsync("xhRoleModuleConfig", new System.Text.Json.Nodes.JsonObject());
                return conf["refreshIntervalSecs"]?.GetValue<int>() ?? 300;
            }
            catch
            {
                return 300;
            }
        }
    }
}
