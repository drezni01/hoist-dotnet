using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Hoist.Infra.Database
{
    /// <summary>xh_preference / xh_user_preference access.</summary>
    public class PrefRepository : IPrefRepository
    {
        private readonly IDbContextFactory<HoistDbContext> factory;

        public PrefRepository(IDbContextFactory<HoistDbContext> factory)
        {
            this.factory = factory;
        }

        public async Task<List<Preference>> ListAsync()
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Preferences.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        }

        public async Task<Preference> FindByNameAsync(string name)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Preferences.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name);
        }

        public async Task<Preference> FindByIdAsync(long id)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Preferences.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        }

        public async Task<bool> NameExistsAsync(string name, long exceptId)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.Preferences.AnyAsync(p => p.Name == name && p.Id != exceptId);
        }

        public async Task<Preference> SaveAsync(Preference pref)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var toSave = new Preference
            {
                Id = pref.Id, Version = pref.Version, Name = pref.Name, Type = pref.Type, DefaultValue = pref.DefaultValue,
                Notes = pref.Notes, LastUpdatedBy = pref.LastUpdatedBy, LastUpdated = pref.LastUpdated, GroupName = pref.GroupName
            };
            if (toSave.Id == 0) ctx.Preferences.Add(toSave);
            else ctx.Preferences.Update(toSave);
            await ctx.SaveChangesAsync();
            pref.Id = toSave.Id;
            pref.Version = toSave.Version;
            pref.LastUpdated = toSave.LastUpdated;
            return pref;
        }

        public async Task DeleteAsync(Preference pref)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var existing = await ctx.Preferences.Include(p => p.UserPreferences).FirstOrDefaultAsync(p => p.Id == pref.Id);
            if (existing == null) return;
            ctx.Preferences.Remove(existing);
            await ctx.SaveChangesAsync();
        }

        public async Task<List<UserPreference>> ListUserPrefsAsync(string username)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.UserPreferences.AsNoTracking().Include(u => u.Preference).Where(u => u.Username == username).ToListAsync();
        }

        public async Task<UserPreference> FindUserPrefAsync(long preferenceId, string username)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.UserPreferences.AsNoTracking().Include(u => u.Preference)
                .FirstOrDefaultAsync(u => u.PreferenceId == preferenceId && u.Username == username);
        }

        public async Task<UserPreference> FindUserPrefByIdAsync(long id)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.UserPreferences.AsNoTracking().Include(u => u.Preference).FirstOrDefaultAsync(u => u.Id == id);
        }

        public async Task<List<UserPreference>> QueryUserPrefsAsync(string prefName, string usernameLike)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var q = ctx.UserPreferences.AsNoTracking().Include(u => u.Preference).AsQueryable();
            if (!string.IsNullOrEmpty(prefName)) q = q.Where(u => u.Preference.Name == prefName);
            if (!string.IsNullOrEmpty(usernameLike)) q = q.Where(u => u.Username.Contains(usernameLike));
            return await q.ToListAsync();
        }

        public async Task<bool> UserPrefExistsAsync(long preferenceId, string username, long exceptId)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            return await ctx.UserPreferences.AnyAsync(u => u.PreferenceId == preferenceId && u.Username == username && u.Id != exceptId);
        }

        public async Task<UserPreference> SaveUserPrefAsync(UserPreference userPref)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            // Save a detached copy so the caller's navigation property is not re-inserted.
            var toSave = new UserPreference
            {
                Id = userPref.Id, Version = userPref.Version, Username = userPref.Username, UserValue = userPref.UserValue,
                LastUpdatedBy = userPref.LastUpdatedBy, LastUpdated = userPref.LastUpdated, PreferenceId = userPref.PreferenceId
            };
            if (toSave.Id == 0) ctx.UserPreferences.Add(toSave);
            else ctx.UserPreferences.Update(toSave);
            await ctx.SaveChangesAsync();
            userPref.Id = toSave.Id;
            userPref.Version = toSave.Version;
            userPref.LastUpdated = toSave.LastUpdated;
            return userPref;
        }

        public async Task DeleteUserPrefAsync(UserPreference userPref)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            await ctx.UserPreferences.Where(u => u.Id == userPref.Id).ExecuteDeleteAsync();
        }

        public async Task DeleteUserPrefsAsync(string username)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            await ctx.UserPreferences.Where(u => u.Username == username).ExecuteDeleteAsync();
        }
    }
}
