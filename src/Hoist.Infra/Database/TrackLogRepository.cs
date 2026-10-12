using System.Linq.Expressions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Hoist.Infra.Database
{
    /// <summary>xh_track_log access.</summary>
    public class TrackLogRepository : ITrackLogRepository
    {
        private readonly IDbContextFactory<HoistDbContext> factory;

        public TrackLogRepository(IDbContextFactory<HoistDbContext> factory)
        {
            this.factory = factory;
        }

        public async Task SaveAsync(TrackLog log)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            ctx.TrackLogs.Add(log);
            await ctx.SaveChangesAsync();
        }

        public async Task<List<TrackLog>> ListAllAsync()
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.TrackLogs.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        }

        public async Task<List<TrackLog>> FindRecentClientErrorsAsync(string username, string loadId, DateTime since, int max)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.TrackLogs.AsNoTracking()
                .Where(t => t.Category == "Client Error" && t.Username == username && t.LoadId == loadId && t.DateCreated >= since)
                .OrderByDescending(t => t.DateCreated)
                .Take(max)
                .ToListAsync();
        }

        public async Task UpdateDataAsync(long id, string data)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var log = await ctx.TrackLogs.FirstOrDefaultAsync(t => t.Id == id);
            if (log == null) return;
            log.Data = data;
            await ctx.SaveChangesAsync();
        }

        public async Task<List<TrackLog>> QueryAsync(DateTime start, DateTime end, Expression<Func<TrackLog, bool>> filter, int maxRows)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var q = ctx.TrackLogs.AsNoTracking().Where(t => t.DateCreated >= start && t.DateCreated < end);
            if (filter != null) q = q.Where(filter);
            return await q.OrderByDescending(t => t.DateCreated).ThenByDescending(t => t.Id).Take(maxRows).ToListAsync();
        }

        public async Task<List<string>> DistinctValuesAsync(Expression<Func<TrackLog, string>> column)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.TrackLogs.AsNoTracking().Select(column).Where(v => v != null).Distinct().OrderBy(v => v).ToListAsync();
        }
    }
}
