using System.Text.Json;
using Hoist.Core.Configuration;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/config.md - xh/getConfig.</summary>
    [TestClass]
    public class TestConfigEndpoints
    {
        [TestMethod]
        public async Task TestGetConfigReturnsClientVisibleConfigs()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("/xh/getConfig"));
            JsonAssert.MatchesFixture(json, "xh.getConfig.json");

            Assert.AreEqual("UTC", json.GetProperty("xhAppTimeZone").GetString());
            Assert.AreEqual(JsonValueKind.False, json.GetProperty("xhEnableImpersonation").ValueKind);
            Assert.AreEqual(-1, json.GetProperty("xhAutoRefreshIntervals").GetProperty("app").GetInt32());
            Assert.AreEqual(10000, json.GetProperty("xhActivityTrackingConfig").GetProperty("maxRows").GetProperty("default").GetInt32());
        }

        [TestMethod]
        public async Task TestGetConfigIncludesAppConfigsAndObscuresPasswords()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.Services.GetRequiredService<ConfigService>().EnsureRequiredConfigsCreatedAsync(new[]
            {
                new ConfigSpec { Name = "appSecret", ValueType = "pwd", DefaultValue = "s3cret", ClientVisible = true },
                new ConfigSpec { Name = "appServerOnly", ValueType = "string", DefaultValue = "x" }
            });
            await host.LoginAsync("alice");

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("/xh/getConfig"));
            Assert.AreEqual("*********", json.GetProperty("appSecret").GetString());
            Assert.IsFalse(json.TryGetProperty("appServerOnly", out _));
        }
    }
}
