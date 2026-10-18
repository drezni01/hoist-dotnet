using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Infra.Database;
using Hoist.Tests.Schema;
using Hoist.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hoist.Tests.Unit
{
    /// <summary>
    /// The write-through caches over the xh_* repositories (docs/decisions/0007-write-through-entity-cache.md).
    /// Queries are counted from EF's command log, so the tests see the SQL that actually ran.
    /// </summary>
    [TestClass]
    public class TestEntityCache
    {
        private TestServices ts;
        private TestHoistSqlLogging.CapturingLoggerProvider capture;

        [TestInitialize]
        public async Task SetUp()
        {
            capture = new TestHoistSqlLogging.CapturingLoggerProvider();
            ts = await TestServices.CreateAsync(
                o => o.Database.LogSql = true,
                s => s.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture)));
        }

        [TestCleanup]
        public void TearDown() => ts.Dispose();

        [TestMethod]
        public async Task TestCachingIsOnByDefault()
        {
            Assert.IsInstanceOfType(ts.Get<IConfigRepository>(), typeof(CachingConfigRepository));
            Assert.IsInstanceOfType(ts.Get<IPrefRepository>(), typeof(CachingPrefRepository));
            Assert.IsInstanceOfType(ts.Get<IRoleRepository>(), typeof(CachingRoleRepository));
            Assert.IsInstanceOfType(ts.Get<IJsonBlobRepository>(), typeof(CachingJsonBlobRepository));

            using var uncached = await TestServices.CreateAsync(o => o.Database.CacheEntities = false);
            Assert.IsInstanceOfType(uncached.Get<IConfigRepository>(), typeof(ConfigRepository));
            Assert.IsInstanceOfType(uncached.Get<IPrefRepository>(), typeof(PrefRepository));
            Assert.IsInstanceOfType(uncached.Get<IRoleRepository>(), typeof(RoleRepository));
            Assert.IsInstanceOfType(uncached.Get<IJsonBlobRepository>(), typeof(JsonBlobRepository));
        }

        //------------------------
        // Config
        //------------------------
        [TestMethod]
        public async Task TestConfigReadsAreServedFromMemory()
        {
            var repo = ts.Get<IConfigRepository>();
            var tz = await repo.FindByNameAsync("xhAppTimeZone");
            capture.Entries.Clear();

            Assert.AreEqual(40, (await repo.ListAsync()).Count);
            Assert.AreEqual("UTC", (await repo.FindByNameAsync("xhAppTimeZone")).Value);
            Assert.AreEqual("UTC", (await repo.FindByNameAsync("XHAPPTIMEZONE")).Value);
            Assert.AreEqual("xhAppTimeZone", (await repo.FindByIdAsync(tz.Id)).Name);
            Assert.IsNull(await repo.FindByNameAsync("noSuchConfig"));
            await ts.Get<ConfigService>().GetClientConfigAsync();

            Assert.AreEqual(0, QueryCount("xh_config"));
        }

        [TestMethod]
        public async Task TestConfigWritesThrough()
        {
            var repo = ts.Get<IConfigRepository>();
            await ts.Get<ConfigService>().SetValueAsync("xhAppTimeZone", "Asia/Tokyo", "admin");
            capture.Entries.Clear();

            var tz = await repo.FindByNameAsync("xhAppTimeZone");
            Assert.AreEqual("Asia/Tokyo", tz.Value);
            Assert.AreEqual(1, tz.Version);
            Assert.AreEqual("admin", tz.LastUpdatedBy);
            Assert.AreEqual(0, QueryCount("xh_config"));

            await repo.DeleteAsync(tz);
            Assert.IsNull(await repo.FindByNameAsync("xhAppTimeZone"));
            Assert.AreEqual(39, (await repo.ListAsync()).Count);
        }

        [TestMethod]
        public async Task TestCallersGetCopies()
        {
            var repo = ts.Get<IConfigRepository>();
            var tz = await repo.FindByNameAsync("xhAppTimeZone");
            tz.Value = "edited but never saved";

            Assert.AreEqual("UTC", (await repo.FindByNameAsync("xhAppTimeZone")).Value);
            Assert.AreEqual("UTC", (await repo.ListAsync()).Single(c => c.Name == "xhAppTimeZone").Value);
        }

        [TestMethod]
        public async Task TestFailedSaveReloadsFromDatabase()
        {
            var repo = ts.Get<IConfigRepository>();
            var stale = await repo.FindByNameAsync("xhAppTimeZone");

            // Another writer changes the row behind the cache.
            await using (var ctx = await ts.Get<IDbContextFactory<HoistDbContext>>().CreateDbContextAsync())
            {
                var row = await ctx.Configs.SingleAsync(c => c.Name == "xhAppTimeZone");
                row.Value = "Europe/Paris";
                await ctx.SaveChangesAsync();
            }
            Assert.AreEqual("UTC", (await repo.FindByNameAsync("xhAppTimeZone")).Value);

            stale.Value = "Asia/Tokyo";
            await Assert.ThrowsExceptionAsync<DbUpdateConcurrencyException>(() => repo.SaveAsync(stale));

            var current = await repo.FindByNameAsync("xhAppTimeZone");
            Assert.AreEqual("Europe/Paris", current.Value);
            Assert.AreEqual(1, current.Version);
        }

        //------------------------
        // Preferences
        //------------------------
        [TestMethod]
        public async Task TestUserPrefsAreServedFromMemory()
        {
            var repo = ts.Get<IPrefRepository>();
            var pref = await repo.SaveAsync(NewPref("testPref"));
            Assert.AreEqual(0, (await repo.ListUserPrefsAsync("bob")).Count);

            await repo.SaveUserPrefAsync(new UserPreference { Username = "bob", UserValue = "b", LastUpdatedBy = "bob", PreferenceId = pref.Id });
            capture.Entries.Clear();

            var bobs = await repo.ListUserPrefsAsync("bob");
            Assert.AreEqual(1, bobs.Count);
            Assert.AreEqual("b", bobs[0].UserValue);
            Assert.AreEqual("testPref", bobs[0].Preference.Name);
            Assert.AreEqual("b", (await repo.FindUserPrefAsync(pref.Id, "bob")).UserValue);
            Assert.IsNull(await repo.FindUserPrefAsync(pref.Id + 1000, "bob"));
            Assert.AreEqual("a", (await repo.FindByNameAsync("testPref")).DefaultValue);

            Assert.AreEqual(0, QueryCount("xh_user_preference"));
            Assert.AreEqual(0, QueryCount("xh_preference"));
        }

        [TestMethod]
        public async Task TestUserPrefsSeeTheirPreferenceChange()
        {
            var repo = ts.Get<IPrefRepository>();
            var pref = await repo.SaveAsync(NewPref("testPref"));
            await repo.SaveUserPrefAsync(new UserPreference { Username = "bob", UserValue = "b", LastUpdatedBy = "bob", PreferenceId = pref.Id });
            await repo.ListUserPrefsAsync("bob");

            pref.Notes = "renamed";
            await repo.SaveAsync(pref);
            Assert.AreEqual("renamed", (await repo.FindUserPrefAsync(pref.Id, "bob")).Preference.Notes);
        }

        [TestMethod]
        public async Task TestDeletingPrefDropsUserValues()
        {
            var repo = ts.Get<IPrefRepository>();
            var pref = await repo.SaveAsync(NewPref("testPref"));
            await repo.SaveUserPrefAsync(new UserPreference { Username = "bob", UserValue = "b", LastUpdatedBy = "bob", PreferenceId = pref.Id });
            Assert.AreEqual(1, (await repo.ListUserPrefsAsync("bob")).Count);

            await repo.DeleteAsync(pref);
            Assert.IsNull(await repo.FindByNameAsync("testPref"));
            Assert.AreEqual(0, (await repo.ListUserPrefsAsync("bob")).Count);
        }

        [TestMethod]
        public async Task TestClearingUserPrefs()
        {
            var repo = ts.Get<IPrefRepository>();
            var pref = await repo.SaveAsync(NewPref("testPref"));
            var bobs = await repo.SaveUserPrefAsync(new UserPreference { Username = "bob", UserValue = "b", LastUpdatedBy = "bob", PreferenceId = pref.Id });
            await repo.SaveUserPrefAsync(new UserPreference { Username = "amy", UserValue = "a", LastUpdatedBy = "amy", PreferenceId = pref.Id });

            await repo.DeleteUserPrefsAsync("amy");
            Assert.AreEqual(0, (await repo.ListUserPrefsAsync("amy")).Count);
            Assert.AreEqual(1, (await repo.ListUserPrefsAsync("bob")).Count);

            // An admin edit that moves a row to another user takes it out of the old user's prefs.
            bobs.Username = "amy";
            await repo.SaveUserPrefAsync(bobs);
            Assert.AreEqual(0, (await repo.ListUserPrefsAsync("bob")).Count);
            Assert.AreEqual(1, (await repo.ListUserPrefsAsync("amy")).Count);

            await repo.DeleteUserPrefAsync(bobs);
            Assert.AreEqual(0, (await repo.ListUserPrefsAsync("amy")).Count);
        }

        //------------------------
        // Roles
        //------------------------
        [TestMethod]
        public async Task TestRolesWriteThrough()
        {
            var repo = ts.Get<IRoleRepository>();
            var role = new Role { Name = "TEST_ROLE", Category = "Test", LastUpdatedBy = "admin" };
            role.Members.Add(DefaultRoleService.NewMember(RoleMemberType.USER, "Bob", "admin"));
            await repo.SaveAsync(role);
            capture.Entries.Clear();

            var found = await repo.FindAsync("test_role");
            Assert.AreEqual("TEST_ROLE", found.Name);
            CollectionAssert.AreEqual(new[] { "bob" }, found.Users.ToArray());
            Assert.AreSame(found, found.Members[0].Role);
            Assert.IsTrue((await repo.ListAsync()).Any(r => r.Name == "TEST_ROLE"));
            Assert.AreEqual(0, QueryCount("xh_role"));

            found.Members.Clear();
            found.Members.Add(DefaultRoleService.NewMember(RoleMemberType.USER, "amy", "admin"));
            await repo.SaveAsync(found);
            var updated = await repo.FindAsync("TEST_ROLE");
            CollectionAssert.AreEqual(new[] { "amy" }, updated.Users.ToArray());
            Assert.AreEqual(1, updated.Version);

            await repo.DeleteAsync("TEST_ROLE");
            Assert.IsNull(await repo.FindAsync("TEST_ROLE"));
        }

        //------------------------
        // JsonBlobs
        //------------------------
        [TestMethod]
        public async Task TestJsonBlobsWriteThrough()
        {
            var repo = ts.Get<IJsonBlobRepository>();
            var blob = await repo.SaveAsync(new JsonBlob { Type = "testType", Name = "one", Owner = "bob", Value = "{}", LastUpdatedBy = "bob" });
            capture.Entries.Clear();

            Assert.AreEqual(blob.Id, (await repo.FindByTokenAsync(blob.Token)).Id);
            Assert.AreEqual(blob.Token, (await repo.FindByIdAsync(blob.Id)).Token);
            Assert.AreEqual(1, (await repo.ListActiveAsync("testType")).Count);
            Assert.AreEqual(1, (await repo.ListActiveAsync("testType")).Count);
            // Nothing had read the table before the save, so the first read loads it, once.
            Assert.AreEqual(1, QueryCount("xh_json_blob"));

            blob.ArchivedDate = 1;
            await repo.SaveAsync(blob);
            Assert.AreEqual(0, (await repo.ListActiveAsync("testType")).Count);
            Assert.IsTrue((await repo.FindByTokenAsync(blob.Token)).Archived);

            Assert.AreEqual(1, await repo.DeleteByNameAndOwnerAsync("one", "bob"));
            Assert.IsNull(await repo.FindByTokenAsync(blob.Token));
        }

        private static Preference NewPref(string name) => new Preference { Name = name, Type = "string", DefaultValue = "a", LastUpdatedBy = "admin" };

        /// <summary>Executed commands that read or wrote the table.</summary>
        private int QueryCount(string table)
        {
            return capture.Entries.Count(e =>
                e.Category == "Microsoft.EntityFrameworkCore.Database.Command" &&
                e.Level == LogLevel.Information &&
                (e.Message.Contains($"\"{table}\"") || e.Message.Contains($"`{table}`")));
        }
    }
}
