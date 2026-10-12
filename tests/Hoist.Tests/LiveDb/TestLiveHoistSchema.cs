using Hoist.Core.Configuration;
using Hoist.Infra;
using Hoist.Infra.Database;
using Hoist.Tests.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Hoist.Tests.LiveDb
{
    /// <summary>
    /// Checks the EF model against a real hoist-core MySQL schema, read-only.
    /// Set the connection string for the dedicated Hoist schema with:
    ///   dotnet user-secrets --project tests/Hoist.Tests set Hoist:Database:ConnectionString "Server=...;Database=...;User=...;Password=..."
    /// Run with: dotnet test --filter TestCategory=LiveDb. Inconclusive when no connection string is set.
    /// </summary>
    [TestClass]
    [TestCategory("LiveDb")]
    public class TestLiveHoistSchema
    {
        private static string ConnectionString =>
            new ConfigurationBuilder().AddUserSecrets<TestLiveHoistSchema>().AddEnvironmentVariables().Build()["Hoist:Database:ConnectionString"];

        [TestMethod]
        public async Task TestEveryMappedColumnExists()
        {
            var cs = ConnectionString;
            if (string.IsNullOrEmpty(cs)) Assert.Inconclusive("No Hoist:Database:ConnectionString user-secret set.");

            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name LIKE 'xh\\_%'";
            var dbColumns = new HashSet<string>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) dbColumns.Add($"{reader.GetString(0)}.{reader.GetString(1)}".ToLowerInvariant());
            }

            var missing = TestHoistSchema.ExpectedColumns
                .SelectMany(kv => kv.Value.Select(c => $"{kv.Key}.{c}"))
                .Where(c => !dbColumns.Contains(c))
                .ToList();
            Assert.AreEqual(0, missing.Count, "Mapped columns missing from the live schema: " + string.Join(", ", missing));
        }

        [TestMethod]
        public async Task TestEveryTableReadsThroughEf()
        {
            var cs = ConnectionString;
            if (string.IsNullOrEmpty(cs)) Assert.Inconclusive("No Hoist:Database:ConnectionString user-secret set.");

            var options = new HoistOptions { Database = new HoistDatabaseOptions { Provider = "MySql", ConnectionString = cs, Migrate = "None" } };
            var sc = new ServiceCollection();
            sc.AddLogging();
            sc.AddSingleton(options);
            sc.AddHoistInfra();
            await using var provider = sc.BuildServiceProvider();
            await using var ctx = await provider.GetRequiredService<IDbContextFactory<HoistDbContext>>().CreateDbContextAsync();

            await ctx.Configs.AsNoTracking().Take(50).ToListAsync();
            await ctx.Preferences.AsNoTracking().Take(50).ToListAsync();
            await ctx.UserPreferences.AsNoTracking().Take(50).ToListAsync();
            await ctx.JsonBlobs.AsNoTracking().Take(50).ToListAsync();
            await ctx.TrackLogs.AsNoTracking().OrderByDescending(t => t.Id).Take(50).ToListAsync();
            await ctx.Roles.AsNoTracking().Include(r => r.Members).Take(50).ToListAsync();
        }
    }
}
