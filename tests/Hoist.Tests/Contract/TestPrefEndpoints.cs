using System.Text;
using System.Text.Json;
using Hoist.Core.Interfaces;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/prefs.md - getPrefs, setPrefs, unsetPrefs, clearUserState.</summary>
    [TestClass]
    public class TestPrefEndpoints
    {
        private static readonly string[] BuiltInPrefs = { "xhAutoRefreshEnabled", "xhIdleDetectionDisabled", "xhLastReadChangelog", "xhShowVersionBar", "xhSizingMode", "xhTheme" };

        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        [TestMethod]
        public async Task TestClientUsernameMustMatchSession()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            var missing = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getPrefs"), 400);
            Assert.AreEqual("SessionMismatchException", missing.GetProperty("name").GetString());
            Assert.AreEqual("Unable to confirm match between client and session user.", missing.GetProperty("message").GetString());
            Assert.IsTrue(missing.GetProperty("isRoutine").GetBoolean());

            var mismatch = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getPrefs?clientUsername=bob"), 400);
            Assert.AreEqual("The reported clientUsername does not match current session user.", mismatch.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task TestGetPrefsReturnsAllWithDefaults()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            // hoist-react posts clientUsername as a form field.
            var response = await host.Client.PostAsync("xh/getPrefs", new FormUrlEncodedContent(new Dictionary<string, string> { ["clientUsername"] = "alice" }));
            var json = await JsonAssert.ReadJsonAsync(response);
            JsonAssert.HasExactKeys(json, BuiltInPrefs);
            JsonAssert.MatchesFixture(json, "xh.getPrefs.json");

            var theme = json.GetProperty("xhTheme");
            Assert.AreEqual("string", theme.GetProperty("type").GetString());
            Assert.AreEqual("system", theme.GetProperty("value").GetString());
            Assert.AreEqual("system", theme.GetProperty("defaultValue").GetString());
            Assert.IsFalse(theme.GetProperty("isSet").GetBoolean());
            Assert.AreEqual(JsonValueKind.Object, json.GetProperty("xhSizingMode").GetProperty("value").ValueKind);
            Assert.AreEqual(JsonValueKind.True, json.GetProperty("xhAutoRefreshEnabled").GetProperty("value").ValueKind);
        }

        [TestMethod]
        public async Task TestSetPrefsReturnsOnlySubmittedKeys()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            var response = await host.Client.PostAsync("xh/setPrefs?clientUsername=alice",
                Json("""{"xhTheme":"dark","xhSizingMode":{"desktop":"compact"},"xhAutoRefreshEnabled":false}"""));
            var json = await JsonAssert.ReadJsonAsync(response);
            JsonAssert.HasExactKeys(json, "preferences");
            var prefs = json.GetProperty("preferences");
            JsonAssert.HasExactKeys(prefs, "xhTheme", "xhSizingMode", "xhAutoRefreshEnabled");
            Assert.AreEqual("dark", prefs.GetProperty("xhTheme").GetProperty("value").GetString());
            Assert.IsTrue(prefs.GetProperty("xhTheme").GetProperty("isSet").GetBoolean());
            Assert.AreEqual("compact", prefs.GetProperty("xhSizingMode").GetProperty("value").GetProperty("desktop").GetString());
            Assert.AreEqual(JsonValueKind.False, prefs.GetProperty("xhAutoRefreshEnabled").GetProperty("value").ValueKind);

            var all = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getPrefs?clientUsername=alice"));
            Assert.AreEqual("dark", all.GetProperty("xhTheme").GetProperty("value").GetString());

            var row = (await host.Services.GetRequiredService<IPrefRepository>().ListUserPrefsAsync("alice")).Single(u => u.Preference.Name == "xhTheme");
            Assert.AreEqual("dark", row.UserValue);
            Assert.AreEqual("alice", row.LastUpdatedBy);
        }

        [TestMethod]
        public async Task TestPrefsAreIsolatedPerUser()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            await host.Client.PostAsync("xh/setPrefs?clientUsername=alice", Json("""{"xhTheme":"dark"}"""));

            var bob = host.NewClient();
            await host.LoginAsync("bob", bob);
            var json = await JsonAssert.ReadJsonAsync(await bob.GetAsync("xh/getPrefs?clientUsername=bob"));
            Assert.AreEqual("system", json.GetProperty("xhTheme").GetProperty("value").GetString());
        }

        [TestMethod]
        public async Task TestSetPrefsValidation()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            var invalid = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/setPrefs?clientUsername=alice", Json("""{"xhAutoRefreshEnabled":"maybe"}""")), 400);
            Assert.AreEqual("ValidationException", invalid.GetProperty("name").GetString());

            var unknown = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/setPrefs?clientUsername=alice", Json("""{"noSuchPref":"x"}""")), 500);
            Assert.AreEqual("Preference not found: noSuchPref", unknown.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task TestUnsetPrefsRestoresDefault()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            await host.Client.PostAsync("xh/setPrefs?clientUsername=alice", Json("""{"xhTheme":"dark","xhShowVersionBar":"never"}"""));

            var json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/unsetPrefs?clientUsername=alice", Json("""["xhTheme"]""")));
            var prefs = json.GetProperty("preferences");
            JsonAssert.HasExactKeys(prefs, "xhTheme");
            Assert.AreEqual("system", prefs.GetProperty("xhTheme").GetProperty("value").GetString());
            Assert.IsFalse(prefs.GetProperty("xhTheme").GetProperty("isSet").GetBoolean());

            var all = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getPrefs?clientUsername=alice"));
            Assert.AreEqual("never", all.GetProperty("xhShowVersionBar").GetProperty("value").GetString());
        }

        [TestMethod]
        public async Task TestClearUserStateRemovesAllUserPrefs()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            await host.Client.PostAsync("xh/setPrefs?clientUsername=alice", Json("""{"xhTheme":"dark","xhShowVersionBar":"never"}"""));

            await JsonAssert.NoContentAsync(await host.Client.PostAsync("xh/clearUserState",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["clientUsername"] = "alice" })));

            var all = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getPrefs?clientUsername=alice"));
            Assert.IsTrue(all.EnumerateObject().All(p => !p.Value.GetProperty("isSet").GetBoolean()));
        }
    }
}
