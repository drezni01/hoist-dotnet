using System.Text.Json;
using System.Text.Json.Nodes;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/environment.md - xh/environment, xh/environmentPoll.</summary>
    [TestClass]
    public class TestEnvironmentEndpoints
    {
        private static readonly string[] EnvKeys =
        {
            "appCode", "appName", "appVersion", "appBuild", "appEnvironment", "grailsVersion", "hoistCoreVersion", "javaVersion",
            "serverTimeZone", "serverTimeZoneOffset", "appTimeZone", "appTimeZoneOffset", "webSocketsEnabled", "instanceName",
            "alertBanner", "pollConfig"
        };

        [TestMethod]
        public async Task TestEnvironmentShape()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/environment"));
            JsonAssert.HasExactKeys(json, EnvKeys);
            Assert.AreEqual("testApp", json.GetProperty("appCode").GetString());
            Assert.AreEqual("Test App", json.GetProperty("appName").GetString());
            Assert.AreEqual("1.2.3", json.GetProperty("appVersion").GetString());
            Assert.AreEqual("Development", json.GetProperty("appEnvironment").GetString());
            Assert.AreEqual("42.1.0", json.GetProperty("hoistCoreVersion").GetString());
            Assert.AreEqual(JsonValueKind.False, json.GetProperty("webSocketsEnabled").ValueKind);
            Assert.AreEqual("test-instance", json.GetProperty("instanceName").GetString());
            Assert.AreEqual("UTC", json.GetProperty("appTimeZone").GetString());
            Assert.AreEqual(0, json.GetProperty("appTimeZoneOffset").GetInt64());
            Assert.AreEqual(JsonValueKind.Number, json.GetProperty("serverTimeZoneOffset").ValueKind);
            Assert.IsFalse(string.IsNullOrEmpty(json.GetProperty("serverTimeZone").GetString()));
            Assert.AreEqual("""{"active":false}""", json.GetProperty("alertBanner").GetRawText());
            Assert.AreEqual("""{"interval":10,"onVersionChange":"promptReload"}""", json.GetProperty("pollConfig").GetRawText());
        }

        [TestMethod]
        public async Task TestAdminReaderSeesDatabaseInfo()
        {
            await using var host = await HoistTestHost.StartAsync(o => o.BootstrapAdminUser = "admin");
            await host.LoginAsync("admin");

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/environment"));
            JsonAssert.HasExactKeys(json, EnvKeys.Concat(new[] { "databaseConnectionString", "databaseUser", "databaseCreateMode" }).ToArray());
            StringAssert.StartsWith(json.GetProperty("databaseConnectionString").GetString(), "Sqlite: ");
            Assert.AreEqual("EnsureCreated", json.GetProperty("databaseCreateMode").GetString());
        }

        [TestMethod]
        public void TestDatabaseInfoStripsPassword()
        {
            var options = HoistTestHost.TestOptions();
            options.Database.Provider = "MySql";
            options.Database.ConnectionString = "Server=db;Database=hoist;User=hoist;Password=topsecret";
            var info = new Hoist.Infra.Database.DatabaseInfo(options);
            Assert.IsFalse(info.ConnectionString.Contains("topsecret"), info.ConnectionString);
            Assert.AreEqual("hoist", info.User);
        }

        [TestMethod]
        public async Task TestAppTimeZoneUsesConfigAndFallsBackToUtc()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var configs = host.Services.GetRequiredService<ConfigService>();

            await configs.SetValueAsync("xhAppTimeZone", "Asia/Tokyo", "test");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/environment"));
            Assert.AreEqual("Asia/Tokyo", json.GetProperty("appTimeZone").GetString());
            Assert.AreEqual(9 * 3600 * 1000, json.GetProperty("appTimeZoneOffset").GetInt64());

            await configs.SetValueAsync("xhAppTimeZone", "Not/AZone", "test");
            json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/environment"));
            Assert.AreEqual("UTC", json.GetProperty("appTimeZone").GetString());
        }

        [TestMethod]
        public async Task TestEnvironmentPollShape()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/environmentPoll"));
            JsonAssert.HasExactKeys(json, "appCode", "appVersion", "appBuild", "instanceName", "alertBanner", "pollConfig");
        }

        [TestMethod]
        public async Task TestActiveAlertBannerIsPublished()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var spec = JsonNode.Parse("""{"active":true,"message":"Maintenance at 5pm","intent":"warning","expires":null}""");
            await host.Services.GetRequiredService<IJsonBlobRepository>().SaveAsync(new JsonBlob
            {
                Type = "xhAlertBanner",
                Name = "xhAlertBanner_Development",
                Owner = "xhAlertBannerService",
                Value = spec.ToJsonString(),
                LastUpdatedBy = "test"
            });
            await host.Services.GetRequiredService<AlertBannerService>().RefreshAsync();

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/environmentPoll"));
            var banner = json.GetProperty("alertBanner");
            Assert.IsTrue(banner.GetProperty("active").GetBoolean());
            Assert.AreEqual("Maintenance at 5pm", banner.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task TestExpiredOrDisabledAlertBannerIsInactive()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var expired = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            await host.Services.GetRequiredService<IJsonBlobRepository>().SaveAsync(new JsonBlob
            {
                Type = "xhAlertBanner",
                Name = "xhAlertBanner_Development",
                Owner = "xhAlertBannerService",
                Value = $$"""{"active":true,"message":"old","expires":{{expired}}}""",
                LastUpdatedBy = "test"
            });
            await host.Services.GetRequiredService<AlertBannerService>().RefreshAsync();

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/environmentPoll"));
            Assert.AreEqual("""{"active":false}""", json.GetProperty("alertBanner").GetRawText());
        }
    }
}
