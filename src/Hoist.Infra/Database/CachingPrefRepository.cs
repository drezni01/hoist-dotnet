using System.Collections.Concurrent;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;

namespace Hoist.Infra.Database
{
    /// <summary>
    /// Write-through cache over xh_preference and xh_user_preference. Preferences are read once; each user's
    /// preference rows are read on that user's first request. A user preference's Preference comes from the
    /// preference cache when it is read, so it never goes stale. Uniqueness checks and the admin-only lookups
    /// (by id, and the filtered query) still read the database.
    /// </summary>
    public class CachingPrefRepository : IPrefRepository
    {
        private readonly IPrefRepository baseRepository;
        private readonly EntityCache<long, Preference> prefs;
        private readonly ConcurrentDictionary<string, EntityCache<long, UserPreference>> userPrefsByUsername =
            new ConcurrentDictionary<string, EntityCache<long, UserPreference>>(StringComparer.OrdinalIgnoreCase);

        public CachingPrefRepository(IPrefRepository baseRepository)
        {
            this.baseRepository = baseRepository;
            prefs = new EntityCache<long, Preference>(baseRepository.ListAsync, p => p.Id, CopyPref);
        }

        //------------------------
        // Preferences
        //------------------------
        public async Task<List<Preference>> ListAsync()
        {
            return (await prefs.ListAsync()).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public Task<Preference> FindByNameAsync(string name)
        {
            return prefs.FirstOrDefaultAsync(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public Task<Preference> FindByIdAsync(long id) => prefs.GetAsync(id);

        public Task<bool> NameExistsAsync(string name, long exceptId) => baseRepository.NameExistsAsync(name, exceptId);

        public async Task<Preference> SaveAsync(Preference pref)
        {
            try
            {
                var saved = await baseRepository.SaveAsync(pref);
                prefs.Put(saved);
                return saved;
            }
            catch
            {
                prefs.Clear();
                throw;
            }
        }

        public async Task DeleteAsync(Preference pref)
        {
            try
            {
                await baseRepository.DeleteAsync(pref);
                prefs.Remove(pref.Id);
                // The database cascades the delete to the users' values.
                foreach (var bucket in userPrefsByUsername.Values) bucket.RemoveWhere(u => u.PreferenceId == pref.Id);
            }
            catch
            {
                ClearAll();
                throw;
            }
        }

        //------------------------
        // User preferences
        //------------------------
        public async Task<List<UserPreference>> ListUserPrefsAsync(string username)
        {
            var rows = await UserPrefs(username).ListAsync();
            foreach (var row in rows) row.Preference = await prefs.GetAsync(row.PreferenceId);
            return rows;
        }

        public async Task<UserPreference> FindUserPrefAsync(long preferenceId, string username)
        {
            var row = await UserPrefs(username).FirstOrDefaultAsync(u => u.PreferenceId == preferenceId);
            if (row != null) row.Preference = await prefs.GetAsync(row.PreferenceId);
            return row;
        }

        public Task<UserPreference> FindUserPrefByIdAsync(long id) => baseRepository.FindUserPrefByIdAsync(id);

        public Task<List<UserPreference>> QueryUserPrefsAsync(string prefName, string usernameLike) =>
            baseRepository.QueryUserPrefsAsync(prefName, usernameLike);

        public Task<bool> UserPrefExistsAsync(long preferenceId, string username, long exceptId) =>
            baseRepository.UserPrefExistsAsync(preferenceId, username, exceptId);

        public async Task<UserPreference> SaveUserPrefAsync(UserPreference userPref)
        {
            try
            {
                var isUpdate = userPref.Id != 0;
                var saved = await baseRepository.SaveUserPrefAsync(userPref);
                // An admin edit can move a row to another username, so drop it from every bucket first.
                if (isUpdate) RemoveUserPref(saved.Id);
                UserPrefs(saved.Username).Put(saved);
                return saved;
            }
            catch
            {
                ClearUserPrefs();
                throw;
            }
        }

        public async Task DeleteUserPrefAsync(UserPreference userPref)
        {
            try
            {
                await baseRepository.DeleteUserPrefAsync(userPref);
                RemoveUserPref(userPref.Id);
            }
            catch
            {
                ClearUserPrefs();
                throw;
            }
        }

        public async Task DeleteUserPrefsAsync(string username)
        {
            try
            {
                await baseRepository.DeleteUserPrefsAsync(username);
                // Keep the bucket loaded, now empty, so the user's next read needs no query.
                UserPrefs(username).RemoveWhere(_ => true);
            }
            catch
            {
                ClearUserPrefs();
                throw;
            }
        }

        //------------------------
        // Implementation
        //------------------------
        private EntityCache<long, UserPreference> UserPrefs(string username)
        {
            return userPrefsByUsername.GetOrAdd(username ?? "", u =>
                new EntityCache<long, UserPreference>(() => baseRepository.ListUserPrefsAsync(u), r => r.Id, CopyUserPref));
        }

        private void RemoveUserPref(long id)
        {
            foreach (var bucket in userPrefsByUsername.Values) bucket.Remove(id);
        }

        private void ClearUserPrefs()
        {
            foreach (var bucket in userPrefsByUsername.Values) bucket.Clear();
        }

        private void ClearAll()
        {
            prefs.Clear();
            ClearUserPrefs();
        }

        // Cached preferences carry no user values, as ListAsync and FindByNameAsync never loaded them.
        private static Preference CopyPref(Preference pref)
        {
            var ret = EntityCopy.Shallow(pref);
            ret.UserPreferences = new List<UserPreference>();
            return ret;
        }

        private static UserPreference CopyUserPref(UserPreference userPref)
        {
            var ret = EntityCopy.Shallow(userPref);
            ret.Preference = null;
            return ret;
        }
    }
}
