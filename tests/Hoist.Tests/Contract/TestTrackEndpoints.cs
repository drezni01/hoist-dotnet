using System.Text;
using System.Text.Json;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/tracking.md - xh/track (activity, client errors, feedback) and the metrics/spans stubs.</summary>
    [TestClass]
    public class TestTrackEndpoints
    {
        private const string ChromeOnWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static async Task<List<TrackLog>> PostAndReadAsync(HoistTestHost host, string entriesJson, string clientUsername = "alice")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"xh/track?clientUsername={clientUsername}") { Content = Json($$"""{"entries":{{entriesJson}}}""") };
            request.Headers.TryAddWithoutValidation("User-Agent", ChromeOnWindows);
            await JsonAssert.NoContentAsync(await host.Client.SendAsync(request));
            await host.Services.GetRequiredService<TrackService>().FlushAsync();
            return await host.Services.GetRequiredService<ITrackLogRepository>().ListAllAsync();
        }

        [TestMethod]
        public async Task TestTrackRequiresClientUsername()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/track", Json("""{"entries":[]}""")), 400);
            Assert.AreEqual("SessionMismatchException", json.GetProperty("name").GetString());
        }

        [TestMethod]
        public async Task TestEntryIsPersistedWithRequestContext()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var ts = DateTimeOffset.Parse("2026-10-01T12:00:00Z").ToUnixTimeMilliseconds();

            var rows = await PostAndReadAsync(host, $$"""
                [{"msg":"Viewed <b>Grid</b>","category":"Navigation","severity":"warn","data":{"rows":5},
                  "correlationId":"c1","timestamp":{{ts}},"elapsed":1234,"clientAppCode":"app","tabId":"tab1","loadId":"load1",
                  "url":"http://localhost:3000/app","appVersion":"9.9.9","clientUsername":"alice"}]
                """);

            var row = rows.Single();
            Assert.AreEqual("alice", row.Username);
            Assert.IsNull(row.Impersonating);
            Assert.AreEqual("Navigation", row.Category);
            Assert.AreEqual("Viewed &lt;b&gt;Grid&lt;/b&gt;", row.Msg);
            Assert.AreEqual("WARN", row.Severity);
            Assert.AreEqual("""{"rows":5}""", row.Data);
            Assert.AreEqual("c1", row.CorrelationId);
            Assert.AreEqual(ts, new DateTimeOffset(row.DateCreated).ToUnixTimeMilliseconds());
            Assert.AreEqual(1234, row.Elapsed);
            Assert.AreEqual("app", row.ClientAppCode);
            Assert.AreEqual("tab1", row.TabId);
            Assert.AreEqual("load1", row.LoadId);
            Assert.AreEqual("http://localhost:3000/app", row.Url);
            Assert.AreEqual("9.9.9", row.AppVersion);
            Assert.AreEqual("Development", row.AppEnvironment);
            Assert.AreEqual("test-instance", row.Instance);
            Assert.AreEqual("Chrome", row.Browser);
            Assert.AreEqual("Windows", row.Device);
            Assert.AreEqual(ChromeOnWindows, row.UserAgent);
        }

        [TestMethod]
        public async Task TestDefaultsAndTruncation()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var longMsg = string.Join(" ", Enumerable.Repeat("word", 100));

            var rows = await PostAndReadAsync(host, $$"""[{"msg":"{{longMsg}}","severity":"bogus","elapsed":600000}]""");

            var row = rows.Single();
            Assert.AreEqual("Default", row.Category);
            Assert.AreEqual("INFO", row.Severity);
            Assert.AreEqual("1.2.3", row.AppVersion);
            Assert.IsNull(row.Elapsed, "elapsed over xhActivityTrackingConfig.maxElapsedMins (2) is dropped");
            Assert.IsTrue(row.Msg.Length <= 255);
            StringAssert.EndsWith(row.Msg, "word...");
            Assert.IsTrue((DateTime.UtcNow - row.DateCreated).Duration() < TimeSpan.FromMinutes(1));
        }

        [TestMethod]
        public async Task TestSeverityLevelsFilterPersistence()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var rows = await PostAndReadAsync(host, """[{"msg":"debug","severity":"DEBUG"},{"msg":"info"},{"msg":"error","severity":"ERROR"}]""");
            CollectionAssert.AreEquivalent(new[] { "info", "error" }, rows.Select(r => r.Msg).ToArray());

            await host.Services.GetRequiredService<ConfigService>().SetValueAsync("xhActivityTrackingConfig",
                """{"levels":[{"username":"alice","category":"Noisy","severity":"DEBUG"},{"username":"*","category":"*","severity":"WARN"}]}""", "test");
            rows = await PostAndReadAsync(host, """[{"msg":"noisy debug","category":"Noisy","severity":"DEBUG"},{"msg":"plain info"}]""");
            Assert.IsTrue(rows.Any(r => r.Msg == "noisy debug"));
            Assert.IsFalse(rows.Any(r => r.Msg == "plain info"));
        }

        [TestMethod]
        public async Task TestLargeDataIsDropped()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var big = new string('x', 3000);
            var rows = await PostAndReadAsync(host, $$$"""[{"msg":"big","data":{"blob":"{{{big}}}"}}]""");
            Assert.IsNull(rows.Single().Data);
        }

        [TestMethod]
        public async Task TestTrackingDisabledPersistsNothing()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            await host.Services.GetRequiredService<ConfigService>().SetValueAsync("xhActivityTrackingConfig", """{"enabled":false}""", "test");
            var rows = await PostAndReadAsync(host, """[{"msg":"ignored"}]""");
            Assert.AreEqual(0, rows.Count);
        }

        [TestMethod]
        public async Task TestClientErrorAndFeedback()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var rows = await PostAndReadAsync(host, """
                [{"msg":"Boom","category":"Client Error","severity":"ERROR","loadId":"L1","data":{"error":{"name":"Error","message":"Boom"},"userAlerted":true},"logData":["userAlerted"]},
                 {"msg":"User submitted feedback","category":"Feedback","data":{"userMessage":"Love it"}}]
                """);
            Assert.AreEqual(2, rows.Count);
            var error = rows.Single(r => r.Category == "Client Error");
            Assert.AreEqual("ERROR", error.Severity);
            Assert.AreEqual("Boom", JsonDocument.Parse(error.Data).RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.AreEqual("Love it", JsonDocument.Parse(rows.Single(r => r.Category == "Feedback").Data).RootElement.GetProperty("userMessage").GetString());
        }

        [TestMethod]
        public async Task TestClientErrorCommentMergesOntoPriorError()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            await PostAndReadAsync(host, """[{"msg":"Boom","category":"Client Error","severity":"ERROR","loadId":"L1","data":{"error":{"name":"Error","message":"Boom"}}}]""");
            var rows = await PostAndReadAsync(host, """[{"msg":"Boom","category":"Client Error","severity":"ERROR","loadId":"L1","data":{"error":{"name":"Error","message":"Boom"},"userMessage":"I clicked save"}}]""");

            var row = rows.Single();
            var data = JsonDocument.Parse(row.Data).RootElement;
            Assert.AreEqual("I clicked save", data.GetProperty("userMessage").GetString());
        }

        [TestMethod]
        public async Task TestServerSideTrack()
        {
            await using var host = await HoistTestHost.StartAsync();
            var svc = host.Services.GetRequiredService<TrackService>();
            await svc.TrackAsync(new TrackEntry { Msg = "Server event", Category = "Audit", Username = "system", Data = new System.Text.Json.Nodes.JsonObject { ["id"] = 7 } });
            await svc.FlushAsync();
            var row = (await host.Services.GetRequiredService<ITrackLogRepository>().ListAllAsync()).Single();
            Assert.AreEqual("system", row.Username);
            Assert.AreEqual("Audit", row.Category);
            Assert.IsNull(row.Browser);
        }

        [TestMethod]
        public async Task TestMetricsAndSpansAreAcceptedAndIgnored()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            await JsonAssert.NoContentAsync(await host.Client.PostAsync("xh/recordMetrics?clientUsername=alice", Json("""{"entries":[{"name":"x"}]}""")));
            await JsonAssert.NoContentAsync(await host.Client.PostAsync("xh/submitSpans?clientUsername=alice", Json("""[{"name":"span"}]""")));
        }
    }
}
