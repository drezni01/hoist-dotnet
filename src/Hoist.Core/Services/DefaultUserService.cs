using System.Collections.Concurrent;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;

namespace Hoist.Core.Services
{
    /// <summary>
    /// Default user directory when the host supplies none. Users come from authentication (token
    /// claims) and are kept in memory. Usernames that appear as USER members of a role are also
    /// known (with minimal details), so they can be listed and impersonated before their first login.
    /// </summary>
    public class DefaultUserService : IHoistUserService, IHoistUserProvisioner
    {
        private readonly ConcurrentDictionary<string, HoistUser> users = new ConcurrentDictionary<string, HoistUser>();
        private readonly IRoleRepository roleRepository;

        public DefaultUserService(IRoleRepository roleRepository)
        {
            this.roleRepository = roleRepository;
        }

        public async Task<HoistUser> FindAsync(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            if (users.TryGetValue(username, out var user)) return user;
            return (await RoleMemberUsernamesAsync()).Contains(username) ? new HoistUser(username) : null;
        }

        public async Task<List<HoistUser>> ListAsync(bool activeOnly)
        {
            var ret = users.Values.ToDictionary(u => u.Username);
            foreach (var name in await RoleMemberUsernamesAsync())
            {
                if (!ret.ContainsKey(name)) ret[name] = new HoistUser(name);
            }
            return ret.Values.Where(u => !activeOnly || u.Active).OrderBy(u => u.Username).ToList();
        }

        public Task<HoistUser> GetOrCreateAsync(HoistUser fromToken)
        {
            return Task.FromResult(users.AddOrUpdate(fromToken.Username, fromToken, (_, _) => fromToken));
        }

        private async Task<HashSet<string>> RoleMemberUsernamesAsync()
        {
            var roles = await roleRepository.ListAsync();
            return roles.SelectMany(r => r.Users).ToHashSet();
        }
    }
}
