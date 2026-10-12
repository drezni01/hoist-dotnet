using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Hoist.Infra.Database
{
    /// <summary>xh_json_blob access.</summary>
    public class JsonBlobRepository : IJsonBlobRepository
    {
        private readonly IDbContextFactory<HoistDbContext> factory;

        public JsonBlobRepository(IDbContextFactory<HoistDbContext> factory)
        {
            this.factory = factory;
        }

        public async Task<JsonBlob> FindByTokenAsync(string token)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.JsonBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Token == token);
        }

        public async Task<JsonBlob> FindByIdAsync(long id)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.JsonBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        }

        public async Task<List<JsonBlob>> ListActiveAsync(string type)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.JsonBlobs.AsNoTracking().Where(b => b.Type == type && b.ArchivedDate == 0).ToListAsync();
        }

        public async Task<List<JsonBlob>> ListAllAsync()
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.JsonBlobs.AsNoTracking().ToListAsync();
        }

        public async Task<bool> NameExistsAsync(JsonBlob blob)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.JsonBlobs.AnyAsync(b =>
                b.Name == blob.Name &&
                b.Type == blob.Type &&
                b.ArchivedDate == blob.ArchivedDate &&
                (blob.Owner == null ? b.Owner == null : b.Owner == blob.Owner) &&
                b.Token != blob.Token);
        }

        public async Task<JsonBlob> SaveAsync(JsonBlob blob)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            if (blob.Id == 0) ctx.JsonBlobs.Add(blob);
            else ctx.JsonBlobs.Update(blob);
            await ctx.SaveChangesAsync();
            return blob;
        }

        public async Task DeleteAsync(JsonBlob blob)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            await ctx.JsonBlobs.Where(b => b.Id == blob.Id).ExecuteDeleteAsync();
        }

        public async Task<int> DeleteByNameAndOwnerAsync(string name, string owner)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.JsonBlobs.Where(b => b.Name == name && b.Owner == owner).ExecuteDeleteAsync();
        }
    }
}
