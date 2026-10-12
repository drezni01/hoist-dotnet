using Hoist.Core.Models;

namespace Hoist.Core.Interfaces
{
    /// <summary>
    /// The identity of the current request - Api implements it from the Hoist session cookie.
    /// Username is the apparent user (the impersonated user while impersonating); AuthUsername is the
    /// user who actually authenticated. Mirrors hoist-core IdentityService.username / authUsername.
    /// </summary>
    public interface IIdentityAccessor
    {
        string Username { get; }
        string AuthUsername { get; }
    }

    /// <summary>Instance config: per-deployment overrides of AppConfig values (InstanceConfigUtils).</summary>
    public interface IInstanceConfig
    {
        /// <summary>The override for key, or null when none is set.</summary>
        string Get(string key);
    }

    /// <summary>Encryption of pwd-type configs - Jasypt-compatible so values are shared with hoist-core.</summary>
    public interface IConfigEncryptor
    {
        string Encrypt(string plainText);
        string Decrypt(string cipherText);

        /// <summary>One-way digest used by the admin console to compare pwd values without exposing them.</summary>
        string Digest(string plainText);
    }

    public interface IConfigRepository
    {
        Task<List<AppConfig>> ListAsync();
        Task<AppConfig> FindByNameAsync(string name);
        Task<AppConfig> FindByIdAsync(long id);
        Task<bool> NameExistsAsync(string name, long exceptId);

        /// <summary>Inserts (Id == 0) or updates the config, flushing immediately.</summary>
        Task<AppConfig> SaveAsync(AppConfig config);
        Task DeleteAsync(AppConfig config);
    }
}
