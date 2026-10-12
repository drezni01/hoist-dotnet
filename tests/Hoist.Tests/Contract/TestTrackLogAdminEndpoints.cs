using System.Text;
using System.Text.Json;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/admin.md - trackLogAdmin (Activity tab) with Hoist filter specs.</summary>
    [TestClass]
    public class TestTrackLogAdminEndpoints
    {
        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static async Task<HoistTestHost> StartWithLogsAsync()
        {
            var host = await HoistTestHost.StartAsync();
            await host.Services.GetRequiredService<DefaultRoleService>().AssignRoleAsync("admin", "HOIST_ADMIN");
            await host.LoginAsync("admin");

            var repo = host.Services.GetRequiredService<ITrackLogRepository>();
            var day = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
            await repo.SaveAsync(new TrackLog { Username = "bob", Category = "Nav", Msg = "Opened Grid", Severity = "INFO", DateCreated = day, Browser = "Chrome", Device = "Windows", AppVersion = "1.0", TabId = "t1", Data = """{"x":1}""" });
            await repo.SaveAsync(new TrackLog { Username = "carol", Category = "Client Error", Msg = "Boom", Severity = "ERROR", DateCreated = day.AddDays(1), Browser = "Firefox", Device = "Mac", AppVersion = "1.1", Elapsed = 50 });
            await repo.SaveAsync(new TrackLog { Username = "bob", Category = "Feedback", Msg = "Nice", Severity = "INFO", DateCreated = day.AddDays(2), Browser = "Chrome", Device = "Windows", AppVersion = "1.1" });
            return host;
        }

        private static async Task<List<JsonElement>> QueryAsync(HoistTestHost host, string body)
        {
            var json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("trackLogAdmin", Json(body)));
            Assert.AreEqual(JsonValueKind.Array, json.ValueKind);
            return json.EnumerateArray().ToList();
        }

        [TestMethod]
        public async Task TestQueryShapeAndOrder()
        {
            await using var host = await StartWithLogsAsync();
            var rows = await QueryAsync(host, """{"startDay":"2026-09-01","endDay":"20260930"}""");
            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(new[] { "Nice", "Boom", "Opened Grid" }, rows.Select(r => r.GetProperty("msg").GetString()).ToArray());

            var row = rows.Last();
            JsonAssert.HasExactKeys(row, "id", "username", "impersonating", "dateCreated", "day", "category", "msg", "data", "elapsed", "severity",
                "correlationId", "loadId", "tabId", "instance", "clientAppCode", "browser", "device", "userAgent", "appVersion", "appEnvironment", "url");
            Assert.AreEqual("2026-09-15", row.GetProperty("day").GetString());
            Assert.AreEqual("""{"x":1}""", row.GetProperty("data").GetString(), "data is the raw JSON string");
            Assert.AreEqual(JsonValueKind.Number, row.GetProperty("dateCreated").ValueKind);
        }

        [TestMethod]
        public async Task TestDayRangeAndMaxRows()
        {
            await using var host = await StartWithLogsAsync();
            Assert.AreEqual(1, (await QueryAsync(host, """{"startDay":"2026-09-16","endDay":"2026-09-16"}""")).Count);
            Assert.AreEqual(2, (await QueryAsync(host, """{"startDay":"2026-09-01","endDay":"2026-09-30","maxRows":2}""")).Count);
            Assert.AreEqual(0, (await QueryAsync(host, """{"startDay":"2026-10-01","endDay":"2026-10-30"}""")).Count);
        }

        [TestMethod]
        public async Task TestFilters()
        {
            await using var host = await StartWithLogsAsync();
            const string range = "\"startDay\":\"2026-09-01\",\"endDay\":\"2026-09-30\"";

            Assert.AreEqual(2, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"field":"username","op":"=","value":"bob"}}""")).Count);
            Assert.AreEqual(3, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"field":"username","op":"=","value":["bob","carol"]}}""")).Count);
            Assert.AreEqual(1, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"field":"username","op":"!=","value":"bob"}}""")).Count);
            Assert.AreEqual(1, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"field":"msg","op":"like","value":"GRID"}}""")).Count);
            Assert.AreEqual(1, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"field":"msg","op":"begins","value":"bo"}}""")).Count);
            Assert.AreEqual(2, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"field":"msg","op":"not ends","value":"om"}}""")).Count);
            Assert.AreEqual(1, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"field":"elapsed","op":">","value":10}}""")).Count);
            Assert.AreEqual(2, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"field":"elapsed","op":"=","value":null}}""")).Count);
            Assert.AreEqual(1, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"filters":[{"field":"username","op":"=","value":"bob"},{"field":"category","op":"=","value":"Nav"}],"op":"AND"}}""")).Count);
            Assert.AreEqual(2, (await QueryAsync(host, $$$"""{{{{range}}},"filters":{"filters":[{"field":"category","op":"=","value":"Nav"},{"field":"category","op":"=","value":"Feedback"}],"op":"OR"}}""")).Count);
            Assert.AreEqual(1, (await QueryAsync(host, $$$"""{{{{range}}},"filters":[{"field":"username","op":"=","value":"bob"},{"field":"browser","op":"=","value":"Chrome"},{"field":"msg","op":"like","value":"nice"}]}""")).Count);
        }

        [TestMethod]
        public async Task TestTabIdDetailQueryWithoutDays()
        {
            await using var host = await StartWithLogsAsync();
            var rows = await QueryAsync(host, """{"filters":{"field":"tabId","op":"=","value":"t1"}}""");
            Assert.AreEqual(1, rows.Count);
        }

        [TestMethod]
        public async Task TestLookups()
        {
            await using var host = await StartWithLogsAsync();
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("trackLogAdmin/lookups"));
            JsonAssert.HasExactKeys(json, "appVersion", "browser", "category", "device", "username");
            Assert.AreEqual("""["Chrome","Firefox"]""", json.GetProperty("browser").GetRawText());
            Assert.AreEqual("""["bob","carol"]""", json.GetProperty("username").GetRawText());
        }

        [TestMethod]
        public async Task TestDisabledTracking()
        {
            await using var host = await StartWithLogsAsync();
            await host.Services.GetRequiredService<ConfigService>().SetValueAsync("xhActivityTrackingConfig", """{"enabled":false}""", "test");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("trackLogAdmin", Json("{}")), 400);
            Assert.AreEqual("DataNotAvailableException", json.GetProperty("name").GetString());
        }
    }
}
