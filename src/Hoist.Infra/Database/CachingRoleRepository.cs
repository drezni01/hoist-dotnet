using Hoist.Core.Interfaces;
using Hoist.Core.Models;

namespace Hoist.Infra.Database
{
    /// <summary>
    /// Write-through cache over xh_role and xh_role_member. Roles and their members are read once and
    /// cached together, keyed by name without regard to case, as the primary key is in MySQL.
    /// </summary>
    public class CachingRoleRepository : IRoleRepository
    {
        private readonly IRoleRepository baseRepository;
        private readonly EntityCache<string, Role> roles;

        public CachingRoleRepository(IRoleRepository baseRepository)
        {
            this.baseRepository = baseRepository;
            roles = new EntityCache<string, Role>(baseRepository.ListAsync, r => r.Name, CopyRole, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<List<Role>> ListAsync()
        {
            return (await roles.ListAsync()).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public Task<Role> FindAsync(string name) => roles.GetAsync(name);

        public async Task<Role> SaveAsync(Role role)
        {
            try
            {
                var saved = await baseRepository.SaveAsync(role);
                roles.Put(saved);
                return saved;
            }
            catch
            {
                roles.Clear();
                throw;
            }
        }

        public async Task DeleteAsync(string name)
        {
            try
            {
                await baseRepository.DeleteAsync(name);
                roles.Remove(name);
            }
            catch
            {
                roles.Clear();
                throw;
            }
        }

        private static Role CopyRole(Role role)
        {
            var ret = EntityCopy.Shallow(role);
            ret.Members = role.Members.Select(m =>
            {
                var member = EntityCopy.Shallow(m);
                member.Role = ret;
                return member;
            }).ToList();
            return ret;
        }
    }
}
