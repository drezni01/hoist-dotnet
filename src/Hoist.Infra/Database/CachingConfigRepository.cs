using Hoist.Core.Interfaces;
using Hoist.Core.Models;

namespace Hoist.Infra.Database
{
    /// <summary>
    /// Write-through cache over xh_config. The table is read once; later reads are served from memory.
    /// Name matches ignore case, as MySQL's default collation does. The uniqueness check still reads the
    /// database, since it guards a write.
    /// </summary>
    public class CachingConfigRepository : IConfigRepository
    {
        private readonly IConfigRepository baseRepository;
        private readonly EntityCache<long, AppConfig> configs;

        public CachingConfigRepository(IConfigRepository baseRepository)
        {
            this.baseRepository = baseRepository;
            configs = new EntityCache<long, AppConfig>(baseRepository.ListAsync, c => c.Id, EntityCopy.Shallow);
        }

        public async Task<List<AppConfig>> ListAsync()
        {
            return (await configs.ListAsync()).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public Task<AppConfig> FindByNameAsync(string name)
        {
            return configs.FirstOrDefaultAsync(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public Task<AppConfig> FindByIdAsync(long id) => configs.GetAsync(id);

        public Task<bool> NameExistsAsync(string name, long exceptId) => baseRepository.NameExistsAsync(name, exceptId);

        public async Task<AppConfig> SaveAsync(AppConfig config)
        {
            try
            {
                var saved = await baseRepository.SaveAsync(config);
                configs.Put(saved);
                return saved;
            }
            catch
            {
                // Most likely a stale version: reload rather than keep serving it.
                configs.Clear();
                throw;
            }
        }

        public async Task DeleteAsync(AppConfig config)
        {
            try
            {
                await baseRepository.DeleteAsync(config);
                configs.Remove(config.Id);
            }
            catch
            {
                configs.Clear();
                throw;
            }
        }
    }
}
