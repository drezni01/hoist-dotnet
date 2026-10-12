using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Services;
using Hoist.Infra.Configuration;
using Hoist.Infra.Database;
using Hoist.Infra.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Infra
{
    public static class HoistInfraServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the EF data layer (MySQL or SQLite) and the infrastructure implementations of Core
        /// interfaces. HoistOptions must be registered too; it is read when first resolved, not here.
        /// </summary>
        public static IServiceCollection AddHoistInfra(this IServiceCollection services)
        {
            services.AddSingleton(sp => new HoistDbTimeZone(ResolveTimeZone(sp.GetRequiredService<HoistOptions>().Database.TimeZone)));
            services.AddSingleton(sp => SqliteKeepAlive.For(sp.GetRequiredService<HoistOptions>().Database));

            services.AddDbContextFactory<HoistDbContext>((sp, o) =>
            {
                var db = sp.GetRequiredService<HoistOptions>().Database;
                if (string.Equals(db.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    sp.GetRequiredService<SqliteKeepAlive>();
                    o.UseSqlite(db.ConnectionString);
                }
                else
                {
                    o.UseMySql(db.ConnectionString, ServerVersion.Parse(db.ServerVersion + "-mysql"));
                }
                o.ReplaceService<IModelCacheKeyFactory, HoistModelCacheKeyFactory>();
                if (!db.LogSql) o.ConfigureWarnings(w => w.Ignore(QuietCommandEvents));
            });

            services.AddSingleton<IHoistSchema, HoistSchema>();
            services.AddSingleton<IConfigRepository, ConfigRepository>();
            services.AddSingleton<IRoleRepository, RoleRepository>();
            services.AddSingleton<IPrefRepository, PrefRepository>();
            services.AddSingleton<IJsonBlobRepository, JsonBlobRepository>();
            services.AddSingleton<ITrackLogRepository, TrackLogRepository>();
            services.AddSingleton<IDatabaseInfo, DatabaseInfo>();
            services.AddSingleton<IInstanceConfig, InstanceConfig>();
            services.AddSingleton<IConfigEncryptor, JasyptEncryptor>();
            return services;
        }

        /// <summary>The routine Database.Command events, silenced unless LogSql. CommandError is kept.</summary>
        private static readonly EventId[] QuietCommandEvents =
        {
            RelationalEventId.CommandCreating,
            RelationalEventId.CommandCreated,
            RelationalEventId.CommandInitialized,
            RelationalEventId.CommandExecuting,
            RelationalEventId.CommandExecuted,
            RelationalEventId.CommandCanceled,
            RelationalEventId.DataReaderClosing,
            RelationalEventId.DataReaderDisposing
        };

        private static TimeZoneInfo ResolveTimeZone(string id)
        {
            if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Local;
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
    }

    /// <summary>A shared in-memory SQLite database lives only while a connection is open - this holds one for the app's lifetime.</summary>
    public sealed class SqliteKeepAlive : IDisposable
    {
        private readonly SqliteConnection connection;

        private SqliteKeepAlive(SqliteConnection connection)
        {
            this.connection = connection;
        }

        /// <summary>Opens the keep-alive connection for an in-memory SQLite database; a no-op otherwise.</summary>
        public static SqliteKeepAlive For(HoistDatabaseOptions db)
        {
            var inMemory = string.Equals(db.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
                && db.ConnectionString?.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase) == true;
            if (!inMemory) return new SqliteKeepAlive(null);

            var connection = new SqliteConnection(db.ConnectionString);
            connection.Open();
            return new SqliteKeepAlive(connection);
        }

        public void Dispose() => connection?.Dispose();
    }

    /// <summary>The model depends on the provider (column types) and the DB time zone (converters).</summary>
    public class HoistModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            var tz = context.GetService<HoistDbTimeZone>()?.TimeZone?.Id;
            return (context.GetType(), context.Database.ProviderName, tz, designTime);
        }
    }
}
