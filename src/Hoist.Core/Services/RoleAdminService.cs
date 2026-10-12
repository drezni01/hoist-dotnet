using System.Text.Json.Nodes;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Utils;

namespace Hoist.Core.Services
{
    /// <summary>
    /// Port of hoist-core DefaultRoleAdminService (list with effective/inherited members) and the write
    /// side of DefaultRoleUpdateService (create, update, delete, bulkCategoryUpdate) for roleAdmin.
    /// </summary>
    public class RoleAdminService
    {
        private readonly IRoleRepository repo;
        private readonly DefaultRoleService roleService;
        private readonly TrackService trackService;
        private readonly IIdentityAccessor identity;

        public RoleAdminService(IRoleRepository repo, DefaultRoleService roleService, TrackService trackService, IIdentityAccessor identity)
        {
            this.repo = repo;
            this.roleService = roleService;
            this.trackService = trackService;
            this.identity = identity;
        }

        public Dictionary<string, object> GetClientConfig() => new Dictionary<string, object>
        {
            ["userAssignmentSupported"] = roleService.UserAssignmentSupported,
            ["directoryGroupsSupported"] = roleService.DirectoryGroupsSupported,
            ["directoryGroupsDescription"] = roleService.DirectoryGroupsDescription
        };

        //------------------------
        // List
        //------------------------
        public async Task<List<Dictionary<string, object>>> ListAsync()
        {
            var roles = await repo.ListAsync();
            var byName = roles.ToDictionary(r => r.Name);
            return roles.Select(role =>
            {
                var effectiveRoles = GetEffectiveRoles(role, byName);
                var effectiveGroups = GetEffectiveMembers(role, effectiveRoles, byName, r => r.DirectoryGroups);
                var effectiveAssignedUsers = GetEffectiveMembers(role, effectiveRoles, byName, r => r.Users);
                return new Dictionary<string, object>
                {
                    ["name"] = role.Name,
                    ["category"] = role.Category,
                    ["notes"] = role.Notes,
                    ["members"] = role.Members.Select(FormatMember).ToList(),
                    ["lastUpdated"] = role.LastUpdated,
                    ["lastUpdatedBy"] = role.LastUpdatedBy,
                    ["inheritedRoles"] = GetInheritedRoles(role, roles).Select(m => m.Format()).ToList(),
                    ["effectiveRoles"] = effectiveRoles.Select(m => m.Format()).ToList(),
                    ["effectiveDirectoryGroups"] = effectiveGroups.Select(m => m.Format()).ToList(),
                    ["effectiveUsers"] = effectiveAssignedUsers.Select(u => new Dictionary<string, object>
                    {
                        ["name"] = u.Name,
                        ["sources"] = u.SourceRoles.Select(r => new Dictionary<string, object> { ["role"] = r }).ToList()
                    }).ToList(),
                    ["errors"] = new Dictionary<string, object> { ["directoryGroups"] = new Dictionary<string, object>() }
                };
            }).ToList();
        }

        public static Dictionary<string, object> FormatRole(Role r) => new Dictionary<string, object>
        {
            ["name"] = r.Name,
            ["category"] = r.Category,
            ["notes"] = r.Notes,
            ["lastUpdated"] = r.LastUpdated,
            ["lastUpdatedBy"] = r.LastUpdatedBy
        };

        public static Dictionary<string, object> FormatMember(RoleMember m) => new Dictionary<string, object>
        {
            ["type"] = m.Type.ToString(),
            ["name"] = m.Name,
            ["dateCreated"] = m.DateCreated,
            ["createdBy"] = m.CreatedBy
        };

        //------------------------
        // Writes
        //------------------------
        public Task<Role> CreateAsync(JsonObject spec) => CreateOrUpdateAsync(spec, isUpdate: false);
        public Task<Role> UpdateAsync(JsonObject spec) => CreateOrUpdateAsync(spec, isUpdate: true);

        public async Task DeleteAsync(string name)
        {
            // Remove ROLE-type memberships pointing at the deleted role.
            foreach (var role in (await repo.ListAsync()).Where(r => r.Members.Any(m => m.Type == RoleMemberType.ROLE && m.Name == name)))
            {
                role.Members.RemoveAll(m => m.Type == RoleMemberType.ROLE && m.Name == name);
                await repo.SaveAsync(role);
            }
            await repo.DeleteAsync(name);
            await TrackAsync($"Deleted role: '{name}'", null);
            await roleService.RefreshAsync();
        }

        public async Task<List<Role>> BulkCategoryUpdateAsync(List<string> roles, string category)
        {
            var updated = new List<Role>();
            foreach (var name in roles)
            {
                var role = await repo.FindAsync(name) ?? throw new HoistException($"Role not found: {name}");
                role.Category = category;
                updated.Add(await repo.SaveAsync(role));
            }
            await TrackAsync($"Updated {roles.Count} roles.", new JsonObject
            {
                ["roles"] = new JsonArray(roles.Select(r => (JsonNode)r).ToArray()),
                ["category"] = category
            });
            return updated;
        }

        private async Task<Role> CreateOrUpdateAsync(JsonObject spec, bool isUpdate)
        {
            var name = Str(spec["name"]);
            var users = StrList(spec["users"]).Select(u => u.ToLowerInvariant()).ToList();
            var groups = StrList(spec["directoryGroups"]);
            var roleNames = StrList(spec["roles"]);
            var authUsername = identity.AuthUsername;

            Role role;
            if (isUpdate)
            {
                role = await repo.FindAsync(name) ?? throw new HoistException($"Role not found: {name}");
                if (spec.ContainsKey("category")) role.Category = Str(spec["category"]);
                if (spec.ContainsKey("notes")) role.Notes = Str(spec["notes"]);
            }
            else
            {
                if ((await repo.ListAsync()).Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new RoutineRuntimeException("Role Name must be case-insensitive unique.");
                }
                role = new Role { Name = name, Category = Str(spec["category"]), Notes = Str(spec["notes"]) };
            }
            role.LastUpdatedBy = authUsername;
            Validate(role);

            var userChanges = UpdateMembers(role, RoleMemberType.USER, users, authUsername);
            var groupChanges = UpdateMembers(role, RoleMemberType.DIRECTORY_GROUP, groups, authUsername);
            var roleChanges = UpdateMembers(role, RoleMemberType.ROLE, roleNames, authUsername);
            role = await repo.SaveAsync(role);

            if (isUpdate)
            {
                await TrackAsync($"Edited role: '{name}'", new JsonObject
                {
                    ["role"] = name,
                    ["category"] = role.Category,
                    ["notes"] = role.Notes,
                    ["addedUsers"] = Arr(userChanges.Added),
                    ["removedUsers"] = Arr(userChanges.Removed),
                    ["addedDirectoryGroups"] = Arr(groupChanges.Added),
                    ["removedDirectoryGroups"] = Arr(groupChanges.Removed),
                    ["addedRoles"] = Arr(roleChanges.Added),
                    ["removedRoles"] = Arr(roleChanges.Removed)
                });
            }
            else
            {
                await TrackAsync($"Created role: '{name}'", new JsonObject
                {
                    ["role"] = name,
                    ["category"] = role.Category,
                    ["notes"] = role.Notes,
                    ["users"] = Arr(userChanges.Added),
                    ["directoryGroups"] = Arr(groupChanges.Added),
                    ["roles"] = Arr(roleChanges.Added)
                });
            }

            await roleService.RefreshAsync();
            return role;
        }

        //------------------------
        // Implementation
        //------------------------
        private static (List<string> Added, List<string> Removed) UpdateMembers(Role role, RoleMemberType type, List<string> members, string createdBy)
        {
            var added = new List<string>();
            var removed = new List<string>();
            foreach (var existing in role.Members.Where(m => m.Type == type).ToList())
            {
                if (!members.Contains(existing.Name))
                {
                    role.Members.Remove(existing);
                    removed.Add(existing.Name);
                }
            }
            foreach (var member in members)
            {
                if (!role.Members.Any(m => m.Type == type && m.Name == member))
                {
                    role.Members.Add(new RoleMember { Type = type, Name = member, CreatedBy = createdBy, RoleName = role.Name });
                    added.Add(member);
                }
            }
            return (added, removed);
        }

        private static void Validate(Role r)
        {
            const string cls = "io.xh.hoist.role.provided.Role";
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(r.Name)) errors.Add(GrailsMessages.Blank("name", cls));
            if (r.Category != null && r.Category.Trim().Length == 0) errors.Add(GrailsMessages.Blank("category", cls));
            else if (r.Category?.Length > 100) errors.Add(GrailsMessages.MaxSize("category", cls, r.Category, 100));
            if (r.Notes?.Length > 1200) errors.Add(GrailsMessages.MaxSize("notes", cls, r.Notes, 1200));
            if (r.LastUpdatedBy?.Length > 50) errors.Add(GrailsMessages.MaxSize("lastUpdatedBy", cls, r.LastUpdatedBy, 50));
            if (errors.Count > 0) throw new ValidationException(errors);
        }

        /// <summary>Roles whose holders also get this role, with the roles that link them (breadth-first).</summary>
        private static List<EffectiveMember> GetEffectiveRoles(Role role, Dictionary<string, Role> byName)
        {
            var ret = new EffectiveMembers();
            var visited = new HashSet<string> { role.Name };
            var toVisit = new Queue<Role>(new[] { role });
            while (toVisit.Count > 0)
            {
                var current = toVisit.Dequeue();
                foreach (var memberName in current.Roles)
                {
                    ret.Get(memberName).SourceRoles.Add(current.Name);
                    if (visited.Add(memberName) && byName.TryGetValue(memberName, out var next)) toVisit.Enqueue(next);
                }
            }
            return ret.Values;
        }

        /// <summary>Roles this role is granted through (the reverse of effective roles).</summary>
        private static List<EffectiveMember> GetInheritedRoles(Role role, List<Role> allRoles)
        {
            var ret = new EffectiveMembers();
            var visited = new HashSet<string> { role.Name };
            var toVisit = new Queue<Role>(new[] { role });
            while (toVisit.Count > 0)
            {
                var current = toVisit.Dequeue();
                foreach (var inherited in allRoles.Where(r => r.Roles.Contains(current.Name)))
                {
                    ret.Get(inherited.Name).SourceRoles.Add(current.Name);
                    if (visited.Add(inherited.Name)) toVisit.Enqueue(inherited);
                }
            }
            return ret.Values;
        }

        private static List<EffectiveMember> GetEffectiveMembers(Role role, List<EffectiveMember> effectiveRoles, Dictionary<string, Role> byName, Func<Role, IEnumerable<string>> names)
        {
            var ret = new EffectiveMembers();
            foreach (var name in names(role)) ret.Get(name).SourceRoles.Add(role.Name);
            foreach (var source in effectiveRoles)
            {
                if (!byName.TryGetValue(source.Name, out var sourceRole)) continue;
                foreach (var name in names(sourceRole)) ret.Get(name).SourceRoles.Add(source.Name);
            }
            return ret.Values;
        }

        private Task TrackAsync(string msg, JsonObject data) =>
            trackService.TrackAsync(new TrackEntry { Msg = msg, Category = "Audit", Data = data });

        private static string Str(JsonNode n) => n == null ? null : JsonUtils.ToStoredString(n);
        private static List<string> StrList(JsonNode n) => (n as JsonArray)?.Where(x => x != null).Select(x => JsonUtils.ToStoredString(x)).ToList() ?? new List<string>();
        private static JsonArray Arr(List<string> items) => new JsonArray(items.Select(i => (JsonNode)i).ToArray());

        private class EffectiveMember
        {
            public string Name { get; set; }
            public List<string> SourceRoles { get; } = new List<string>();
            public Dictionary<string, object> Format() => new Dictionary<string, object> { ["name"] = Name, ["sourceRoles"] = SourceRoles };
        }

        /// <summary>Insertion-ordered map of name to member, like Groovy's [:].withDefault.</summary>
        private class EffectiveMembers
        {
            private readonly List<EffectiveMember> list = new List<EffectiveMember>();

            public EffectiveMember Get(string name)
            {
                var m = list.FirstOrDefault(x => x.Name == name);
                if (m == null)
                {
                    m = new EffectiveMember { Name = name };
                    list.Add(m);
                }
                return m;
            }

            public List<EffectiveMember> Values => list;
        }
    }
}
