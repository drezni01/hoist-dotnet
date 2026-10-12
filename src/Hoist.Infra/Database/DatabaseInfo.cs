using System.Data.Common;
using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;

namespace Hoist.Infra.Database
{
    /// <summary>The Hoist connection string with any password removed, for xh/environment (admin readers only).</summary>
    public class DatabaseInfo : IDatabaseInfo
    {
        private static readonly string[] SecretKeys = { "password", "pwd" };
        private static readonly string[] UserKeys = { "user id", "userid", "user", "uid", "username" };

        public DatabaseInfo(HoistOptions options)
        {
            var builder = new DbConnectionStringBuilder();
            try
            {
                builder.ConnectionString = options.Database.ConnectionString ?? "";
            }
            catch (ArgumentException)
            {
                builder.Clear();
            }

            foreach (var key in builder.Keys.Cast<string>().ToList())
            {
                if (SecretKeys.Contains(key.ToLowerInvariant())) builder.Remove(key);
                else if (User == null && UserKeys.Contains(key.ToLowerInvariant())) User = builder[key]?.ToString();
            }
            ConnectionString = $"{options.Database.Provider}: {builder.ConnectionString}";
            CreateMode = options.Database.Migrate;
        }

        public string ConnectionString { get; }
        public string User { get; }
        public string CreateMode { get; }
    }
}
