using Hoist.Core.Models;

namespace Hoist.Core.Interfaces
{
    /// <summary>
    /// Port of hoist-core BaseUserService - the app's user directory. A host app replaces the default
    /// (DefaultUserService) by registering its own implementation in DI, e.g. over the host app's users.
    /// </summary>
    public interface IHoistUserService
    {
        /// <summary>The user, or null. Called on every request - must be fast.</summary>
        Task<HoistUser> FindAsync(string username);

        Task<List<HoistUser>> ListAsync(bool activeOnly);

        /// <summary>
        /// BaseUserService.impersonationTargetsForUser: the users authUser may impersonate. Override to
        /// narrow the list; returned users must also be found by FindAsync. IdentityService always applies
        /// the security filter on top: only impersonators get targets, and only Hoist admins may target admins.
        /// </summary>
        Task<List<HoistUser>> ImpersonationTargetsAsync(HoistUser authUser) => ListAsync(true);
    }

    /// <summary>
    /// Optional: lets the authentication service create or update a user from token claims, as a
    /// Grails pass-through UserService (getOrCreatePassThroughUser) does. When the registered
    /// IHoistUserService does not implement this, authentication only accepts users FindAsync knows.
    /// </summary>
    public interface IHoistUserProvisioner
    {
        Task<HoistUser> GetOrCreateAsync(HoistUser fromToken);
    }

    /// <summary>Port of hoist-core BaseRoleService.</summary>
    public interface IRoleService
    {
        Task<Dictionary<string, HashSet<string>>> GetAllRoleAssignmentsAsync();
        Task<HashSet<string>> GetRolesForUserAsync(string username);
        Task<HashSet<string>> GetUsersForRoleAsync(string role);
    }

    public interface IRoleRepository
    {
        /// <summary>All roles with their members.</summary>
        Task<List<Role>> ListAsync();
        Task<Role> FindAsync(string name);

        /// <summary>Inserts a new role (with members) or updates an existing one, replacing its members.</summary>
        Task<Role> SaveAsync(Role role);
        Task DeleteAsync(string name);
    }
}
