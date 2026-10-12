using System.Text.Json;
using Hoist.Tests.Support;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/core.md - ping, version, notFound and the error body shape.</summary>
    [TestClass]
    public class TestCoreEndpoints
    {
        [TestMethod]
        public async Task TestPing()
        {
            await using var host = await HoistTestHost.StartAsync();
            foreach (var url in new[] { "/xh/ping", "/ping" })
            {
                var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync(url));
                JsonAssert.HasExactKeys(json, "appCode", "timestamp", "success");
                Assert.AreEqual("testApp", json.GetProperty("appCode").GetString());
                Assert.AreEqual(JsonValueKind.Number, json.GetProperty("timestamp").ValueKind);
                Assert.IsTrue(json.GetProperty("success").GetBoolean());
            }
        }

        [TestMethod]
        public async Task TestPingAcceptsPost()
        {
            await using var host = await HoistTestHost.StartAsync();
            var response = await host.Client.PostAsync("/xh/ping", new FormUrlEncodedContent(new Dictionary<string, string>()));
            await JsonAssert.ReadJsonAsync(response);
        }

        [TestMethod]
        public async Task TestVersion()
        {
            await using var host = await HoistTestHost.StartAsync();
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("/xh/version"));
            JsonAssert.HasExactKeys(json, "appCode", "appVersion", "appBuild");
            Assert.AreEqual("1.2.3", json.GetProperty("appVersion").GetString());
            Assert.AreEqual("abc123", json.GetProperty("appBuild").GetString());
        }

        [TestMethod]
        public async Task TestUnknownRouteIsNotFound()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("/xh/noSuchAction"), 404);
            // isRoutine is false for NotFoundException, so it is dropped along with the other falsy values.
            JsonAssert.HasExactKeys(json, "name", "message");
            Assert.AreEqual("NotFoundException", json.GetProperty("name").GetString());
            Assert.AreEqual("Not Found", json.GetProperty("message").GetString());
        }
    }
}
