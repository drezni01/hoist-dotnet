using Hoist.Infra.Database;
using Hoist.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Schema
{
    /// <summary>
    /// The EF model must name tables and columns exactly as GORM did for hoist-core v42 (docs/schema/README.md),
    /// so the plugin can read and write an existing hoist-core schema.
    /// </summary>
    [TestClass]
    public class TestHoistSchema
    {
        public static readonly Dictionary<string, string[]> ExpectedColumns = new Dictionary<string, string[]>
        {
            ["xh_config"] = new[] { "id", "version", "name", "value", "value_type", "note", "client_visible", "last_updated_by", "last_updated", "group_name" },
            ["xh_preference"] = new[] { "id", "version", "name", "type", "default_value", "notes", "last_updated_by", "last_updated", "group_name" },
            ["xh_user_preference"] = new[] { "id", "version", "username", "user_value", "last_updated_by", "last_updated", "preference_id" },
            ["xh_json_blob"] = new[] { "id", "version", "token", "type", "owner", "acl", "name", "value", "meta", "description", "date_created", "last_updated", "last_updated_by", "archived_date" },
            ["xh_track_log"] = new[]
            {
                "id", "version", "username", "impersonating", "date_created", "category", "msg", "data", "elapsed", "severity", "correlation_id",
                "load_id", "tab_id", "instance", "client_app_code", "browser", "device", "user_agent", "app_version", "app_environment", "url"
            },
            ["xh_role"] = new[] { "name", "version", "category", "notes", "last_updated", "last_updated_by" },
            ["xh_role_member"] = new[] { "id", "version", "type", "name", "date_created", "created_by", "role_id" }
        };

        [TestMethod]
        public async Task TestModelMatchesGormNaming()
        {
            using var ts = await TestServices.CreateAsync();
            await using var ctx = await ts.Get<IDbContextFactory<HoistDbContext>>().CreateDbContextAsync();

            var actual = ctx.Model.GetEntityTypes().ToDictionary(
                e => e.GetTableName(),
                e => e.GetProperties().Select(p => p.GetColumnName()).OrderBy(c => c).ToArray());

            CollectionAssert.AreEquivalent(ExpectedColumns.Keys.ToArray(), actual.Keys.ToArray());
            foreach (var (table, columns) in ExpectedColumns)
            {
                CollectionAssert.AreEqual(columns.OrderBy(c => c).ToArray(), actual[table], $"Columns differ for {table}");
            }
        }

        [TestMethod]
        public async Task TestVersionIsOptimisticLock()
        {
            using var ts = await TestServices.CreateAsync();
            var factory = ts.Get<IDbContextFactory<HoistDbContext>>();

            await using var a = await factory.CreateDbContextAsync();
            await using var b = await factory.CreateDbContextAsync();
            var ca = await a.Configs.SingleAsync(c => c.Name == "xhAppTimeZone");
            var cb = await b.Configs.SingleAsync(c => c.Name == "xhAppTimeZone");

            ca.Value = "Asia/Tokyo";
            await a.SaveChangesAsync();
            Assert.AreEqual(1, ca.Version);

            cb.Value = "Europe/Paris";
            await Assert.ThrowsExceptionAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        }

        [TestMethod]
        public async Task TestDatesRoundTripAsUtc()
        {
            using var ts = await TestServices.CreateAsync(o => o.Database.TimeZone = "Eastern Standard Time");
            await using var ctx = await ts.Get<IDbContextFactory<HoistDbContext>>().CreateDbContextAsync();
            var config = await ctx.Configs.AsNoTracking().SingleAsync(c => c.Name == "xhAppTimeZone");
            Assert.AreEqual(DateTimeKind.Utc, config.LastUpdated.Kind);
            Assert.IsTrue((DateTime.UtcNow - config.LastUpdated).Duration() < TimeSpan.FromMinutes(1), config.LastUpdated.ToString("O"));
        }
    }
}
