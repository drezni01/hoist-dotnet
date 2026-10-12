using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Hoist.Infra.Database
{
    /// <summary>xh_config access. Each call uses a short-lived context, as is usual for repositories.</summary>
    public class ConfigRepository : IConfigRepository
    {
        private readonly IDbContextFactory<HoistDbContext> factory;

        public ConfigRepository(IDbContextFactory<HoistDbContext> factory)
        {
            this.factory = factory;
        }

        public async Task<List<AppConfig>> ListAsync()
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Configs.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        }

        public async Task<AppConfig> FindByNameAsync(string name)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Configs.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name);
        }

        public async Task<AppConfig> FindByIdAsync(long id)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Configs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        }

        public async Task<bool> NameExistsAsync(string name, long exceptId)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Configs.AnyAsync(c => c.Name == name && c.Id != exceptId);
        }

        public async Task<AppConfig> SaveAsync(AppConfig config)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            if (config.Id == 0) ctx.Configs.Add(config);
            else ctx.Configs.Update(config);
            await ctx.SaveChangesAsync();
            return config;
        }

        public async Task DeleteAsync(AppConfig config)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            ctx.Configs.Remove(config);
            await ctx.SaveChangesAsync();
        }
    }
}
