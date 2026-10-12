using Hoist.Core.Models;

namespace Hoist.Core.Interfaces
{
    public interface IPrefRepository
    {
        Task<List<Preference>> ListAsync();
        Task<Preference> FindByNameAsync(string name);
        Task<Preference> FindByIdAsync(long id);
        Task<bool> NameExistsAsync(string name, long exceptId);
        Task<Preference> SaveAsync(Preference pref);
        Task DeleteAsync(Preference pref);

        /// <summary>The user's preference rows, with Preference loaded.</summary>
        Task<List<UserPreference>> ListUserPrefsAsync(string username);
        Task<UserPreference> FindUserPrefAsync(long preferenceId, string username);
        Task<UserPreference> FindUserPrefByIdAsync(long id);

        /// <summary>All user preferences matching an optional exact pref name and a username substring, with Preference loaded.</summary>
        Task<List<UserPreference>> QueryUserPrefsAsync(string prefName, string usernameLike);
        Task<bool> UserPrefExistsAsync(long preferenceId, string username, long exceptId);
        Task<UserPreference> SaveUserPrefAsync(UserPreference userPref);
        Task DeleteUserPrefAsync(UserPreference userPref);
        Task DeleteUserPrefsAsync(string username);
    }

    public interface IJsonBlobRepository
    {
        Task<JsonBlob> FindByTokenAsync(string token);
        Task<JsonBlob> FindByIdAsync(long id);

        /// <summary>Active (archivedDate == 0) blobs of a type.</summary>
        Task<List<JsonBlob>> ListActiveAsync(string type);

        Task<List<JsonBlob>> ListAllAsync();

        /// <summary>True if another blob (by token) has the same name, type, archivedDate and owner.</summary>
        Task<bool> NameExistsAsync(JsonBlob blob);

        Task<JsonBlob> SaveAsync(JsonBlob blob);
        Task DeleteAsync(JsonBlob blob);

        /// <summary>Hard-deletes blobs by name and owner (any type).</summary>
        Task<int> DeleteByNameAndOwnerAsync(string name, string owner);
    }

    /// <summary>Connection details shown to admin readers in xh/environment (never the password).</summary>
    public interface IDatabaseInfo
    {
        string ConnectionString { get; }
        string User { get; }
        string CreateMode { get; }
    }
}
