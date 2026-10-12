using Hoist.Core.Exceptions;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Hoist.Tests.Schema
{
    /// <summary>
    /// At startup the live schema is checked against the EF model: missing tables or columns are logged
    /// and fail startup, so a schema that drifted (e.g. a hoist-core upgrade not applied) is caught
    /// before the first query instead of as a SQL error later.
    /// </summary>
    [TestClass]
    public class TestHoistSchemaValidation
    {
        [TestMethod]
        public async Task TestCreatedSchemaIsValid()
        {
            using var ts = await TestServices.CreateAsync();
            var problems = await ts.Get<IHoistSchema>().FindProblemsAsync();
            Assert.AreEqual(0, problems.Count, string.Join(", ", problems));
        }

        [TestMethod]
        public async Task TestMissingColumnFailsStartup()
        {
            using var ts = await TestServices.CreateAsync();
            Execute(ts.Options.Database.ConnectionString, "ALTER TABLE xh_config DROP COLUMN note");

            var ex = await Assert.ThrowsExactlyAsync<HoistSchemaException>(() => ts.Get<IHoistSchema>().EnsureSchemaAsync());
            StringAssert.Contains(ex.Message, "xh_config.note");
        }

        [TestMethod]
        public async Task TestMissingTableFailsStartup()
        {
            using var ts = await TestServices.CreateAsync();
            Execute(ts.Options.Database.ConnectionString, "DROP TABLE xh_track_log");

            var problems = await ts.Get<IHoistSchema>().FindProblemsAsync();
            CollectionAssert.Contains(problems.ToList(), "missing table xh_track_log");
            await Assert.ThrowsExactlyAsync<HoistSchemaException>(() => ts.Get<IHoistSchema>().EnsureSchemaAsync());
        }

        [TestMethod]
        public async Task TestExtraColumnIsAllowed()
        {
            using var ts = await TestServices.CreateAsync();
            Execute(ts.Options.Database.ConnectionString, "ALTER TABLE xh_config ADD COLUMN added_later TEXT");

            var problems = await ts.Get<IHoistSchema>().FindProblemsAsync();
            Assert.AreEqual(0, problems.Count, string.Join(", ", problems));
        }

        [TestMethod]
        public async Task TestPartialSchemaFailsServerStartup()
        {
            // An existing schema with only some xh_* tables: EnsureCreated leaves it alone, validation rejects it.
            var cs = $"Data Source=hoist-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();
            Execute(keepAlive, "CREATE TABLE xh_config (id INTEGER PRIMARY KEY, name TEXT)");

            var ex = await Assert.ThrowsExactlyAsync<HoistSchemaException>(() =>
                HoistTestHost.StartAsync(o => o.Database.ConnectionString = cs));
            StringAssert.Contains(ex.Message, "missing table xh_preference");
            StringAssert.Contains(ex.Message, "xh_config.value");
        }

        [TestMethod]
        public async Task TestValidationCanBeDisabled()
        {
            using var ts = await TestServices.CreateAsync(o => o.Database.ValidateSchema = false);
            Execute(ts.Options.Database.ConnectionString, "ALTER TABLE xh_config DROP COLUMN note");

            await ts.Get<IHoistSchema>().EnsureSchemaAsync();
        }

        private static void Execute(string connectionString, string sql)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            Execute(conn, sql);
        }

        private static void Execute(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
