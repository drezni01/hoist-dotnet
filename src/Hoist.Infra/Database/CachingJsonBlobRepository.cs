using Hoist.Core.Interfaces;
using Hoist.Core.Models;

namespace Hoist.Infra.Database
{
    /// <summary>
    /// Write-through cache over xh_json_blob. The table is read once; later reads are served from memory.
    /// Tokens match exactly. Name and owner matches ignore case, as MySQL's default collation does. The
    /// uniqueness check still reads the database, since it guards a write.
    /// </summary>
    public class CachingJsonBlobRepository : IJsonBlobRepository
    {
        private readonly IJsonBlobRepository baseRepository;
        private readonly EntityCache<long, JsonBlob> blobs;

        public CachingJsonBlobRepository(IJsonBlobRepository baseRepository)
        {
            this.baseRepository = baseRepository;
            blobs = new EntityCache<long, JsonBlob>(baseRepository.ListAllAsync, b => b.Id, EntityCopy.Shallow);
        }

        public Task<JsonBlob> FindByTokenAsync(string token) => blobs.FirstOrDefaultAsync(b => b.Token == token);

        public Task<JsonBlob> FindByIdAsync(long id) => blobs.GetAsync(id);

        public async Task<List<JsonBlob>> ListActiveAsync(string type)
        {
            // Id order, as the database returns them.
            return (await blobs.WhereAsync(b => b.ArchivedDate == 0 && string.Equals(b.Type, type, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(b => b.Id).ToList();
        }

        public async Task<List<JsonBlob>> ListAllAsync()
        {
            return (await blobs.ListAsync()).OrderBy(b => b.Id).ToList();
        }

        public Task<bool> NameExistsAsync(JsonBlob blob) => baseRepository.NameExistsAsync(blob);

        public async Task<JsonBlob> SaveAsync(JsonBlob blob)
        {
            try
            {
                var saved = await baseRepository.SaveAsync(blob);
                blobs.Put(saved);
                return saved;
            }
            catch
            {
                blobs.Clear();
                throw;
            }
        }

        public async Task DeleteAsync(JsonBlob blob)
        {
            try
            {
                await baseRepository.DeleteAsync(blob);
                blobs.Remove(blob.Id);
            }
            catch
            {
                blobs.Clear();
                throw;
            }
        }

        public async Task<int> DeleteByNameAndOwnerAsync(string name, string owner)
        {
            try
            {
                var count = await baseRepository.DeleteByNameAndOwnerAsync(name, owner);
                blobs.RemoveWhere(b =>
                    string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(b.Owner, owner, StringComparison.OrdinalIgnoreCase));
                return count;
            }
            catch
            {
                blobs.Clear();
                throw;
            }
        }
    }
}
