using System.Text;
using System.Text.Json;
using Hoist.Core.Interfaces;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/views.md - xhView/* (ViewManager).</summary>
    [TestClass]
    public class TestViewEndpoints
    {
        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static FormUrlEncodedContent Form(params (string k, string v)[] values) =>
            new FormUrlEncodedContent(values.ToDictionary(x => x.k, x => x.v));

        private static async Task<JsonElement> AllDataAsync(HttpClient client, string type = "grid", string instance = "default") =>
            await JsonAssert.ReadJsonAsync(await client.PostAsync("xhView/allData", Form(("type", type), ("viewInstance", instance))));

        private static async Task<string> CreateViewAsync(HttpClient client, string name, string extra = "")
        {
            var json = await JsonAssert.ReadJsonAsync(await client.PostAsync("xhView/create",
                Json($$"""{"type":"grid","name":"{{name}}","group":"G1","description":"d","isShared":false,"isGlobal":false,"value":{"cols":1}{{extra}}}""")));
            return json.GetProperty("token").GetString();
        }

        [TestMethod]
        public async Task TestAllDataWhenEmpty()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var json = await AllDataAsync(host.Client);
            JsonAssert.HasExactKeys(json, "state", "views", "manageGlobal");
            Assert.AreEqual("""{"userPinned":{},"autoSave":false}""", json.GetProperty("state").GetRawText());
            Assert.AreEqual(0, json.GetProperty("views").GetArrayLength());
            Assert.IsFalse(json.GetProperty("manageGlobal").GetBoolean());
        }

        [TestMethod]
        public async Task TestManageGlobalForAdmin()
        {
            await using var host = await HoistTestHost.StartAsync(o => o.BootstrapAdminUser = "admin");
            await host.LoginAsync("admin");
            Assert.IsTrue((await AllDataAsync(host.Client)).GetProperty("manageGlobal").GetBoolean());
        }

        [TestMethod]
        public async Task TestCreateViewAndPin()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            var view = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xhView/create",
                Json("""{"type":"grid","name":"V1","group":"G1","description":"d","isShared":true,"isGlobal":false,"isPinned":true,"value":{"cols":1}}""")));
            JsonAssert.HasExactKeys(view, TestJsonBlobEndpoints.ClientKeys);
            Assert.AreEqual("alice", view.GetProperty("owner").GetString());
            Assert.AreEqual("*", view.GetProperty("acl").GetString());
            Assert.AreEqual("""{"group":"G1","isShared":true}""", view.GetProperty("meta").GetRawText());
            var token = view.GetProperty("token").GetString();

            var all = await AllDataAsync(host.Client);
            var views = all.GetProperty("views");
            Assert.AreEqual(1, views.GetArrayLength(), "the xhUserState blob is not listed as a view");
            Assert.IsFalse(views[0].TryGetProperty("value", out _));
            Assert.IsTrue(all.GetProperty("state").GetProperty("userPinned").GetProperty(token).GetBoolean());

            var got = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xhView/get", Form(("token", token))));
            Assert.AreEqual(1, got.GetProperty("value").GetProperty("cols").GetInt32());

            var tracked = await TrackedViewsAsync(host);
            CollectionAssert.Contains(tracked, "Created View");
        }

        [TestMethod]
        public async Task TestUpdateState()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var token = await CreateViewAsync(host.Client, "V1");

            var state = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xhView/updateState?type=grid&viewInstance=main",
                Json($$$"""{"currentView":"{{{token}}}","autoSave":true,"userPinned":{"{{{token}}}":true,"deadbeef":true}}""")));
            JsonAssert.HasExactKeys(state, "userPinned", "autoSave", "currentView");
            Assert.AreEqual(token, state.GetProperty("currentView").GetString());
            Assert.IsTrue(state.GetProperty("autoSave").GetBoolean());
            JsonAssert.HasExactKeys(state.GetProperty("userPinned"), token);

            // currentView is per viewInstance: absent for another instance, null when set to the default view.
            Assert.IsFalse((await AllDataAsync(host.Client, instance: "other")).GetProperty("state").TryGetProperty("currentView", out _));
            await host.Client.PostAsync("xhView/updateState?type=grid&viewInstance=main", Json("""{"currentView":null}"""));
            Assert.AreEqual(JsonValueKind.Null, (await AllDataAsync(host.Client, instance: "main")).GetProperty("state").GetProperty("currentView").ValueKind);
        }

        [TestMethod]
        public async Task TestUpdateInfoSharingAndGlobal()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var token = await CreateViewAsync(host.Client, "V1");

            var shared = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync($"xhView/updateInfo?token={token}", Json("""{"name":"Renamed","group":"G2","isShared":true}""")));
            Assert.AreEqual("Renamed", shared.GetProperty("name").GetString());
            Assert.AreEqual("*", shared.GetProperty("acl").GetString());
            Assert.AreEqual("""{"group":"G2","isShared":true}""", shared.GetProperty("meta").GetRawText());

            var unshared = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync($"xhView/updateInfo?token={token}", Json("""{"isShared":false}""")));
            Assert.AreEqual(JsonValueKind.Null, unshared.GetProperty("acl").ValueKind);
            Assert.IsFalse(unshared.GetProperty("meta").GetProperty("isShared").GetBoolean());

            var denied = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync($"xhView/updateInfo?token={token}", Json("""{"isGlobal":true}""")), 403);
            Assert.AreEqual("NotAuthorizedException", denied.GetProperty("name").GetString());
        }

        [TestMethod]
        public async Task TestUpdateValueBulkUpdateRenameGroupAndDelete()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var t1 = await CreateViewAsync(host.Client, "V1");
            var t2 = await CreateViewAsync(host.Client, "V2");

            var updated = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync($"xhView/updateValue?token={t1}", Json("""{"cols":9}""")));
            Assert.AreEqual(9, updated.GetProperty("value").GetProperty("cols").GetInt32());

            await JsonAssert.NoContentAsync(await host.Client.PostAsync($"xhView/bulkUpdateInfo?tokens={t1},{t2}", Json("""{"group":"G1/Sub","isPinned":true}""")));
            var all = await AllDataAsync(host.Client);
            Assert.IsTrue(all.GetProperty("views").EnumerateArray().All(v => v.GetProperty("meta").GetProperty("group").GetString() == "G1/Sub"));
            JsonAssert.HasExactKeys(all.GetProperty("state").GetProperty("userPinned"), t1, t2);

            var renamed = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xhView/renameGroup?type=grid", Json("""{"from":"G1","to":"Top","isGlobal":false}""")));
            Assert.AreEqual("""{"count":2}""", renamed.GetRawText());
            all = await AllDataAsync(host.Client);
            Assert.IsTrue(all.GetProperty("views").EnumerateArray().All(v => v.GetProperty("meta").GetProperty("group").GetString() == "Top/Sub"));

            await JsonAssert.NoContentAsync(await host.Client.PostAsync($"xhView/delete?tokens={t1},{t2}", null));
            Assert.AreEqual(0, (await AllDataAsync(host.Client)).GetProperty("views").GetArrayLength());
        }

        [TestMethod]
        public async Task TestBulkFailuresAreReported()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var t1 = await CreateViewAsync(host.Client, "V1");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync($"xhView/delete?tokens={t1},nosuchtk", null), 500);
            StringAssert.StartsWith(json.GetProperty("message").GetString(), "Failed to delete 1 view(s) - Active JsonBlob not found");
            Assert.AreEqual(0, (await AllDataAsync(host.Client)).GetProperty("views").GetArrayLength(), "the good token is still archived");
        }

        [TestMethod]
        public async Task TestClearUserStateRemovesViewState()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var token = await CreateViewAsync(host.Client, "V1", ""","isPinned":true""");

            await JsonAssert.NoContentAsync(await host.Client.PostAsync("xh/clearUserState", Form(("clientUsername", "alice"))));
            var all = await AllDataAsync(host.Client);
            Assert.AreEqual("""{"userPinned":{},"autoSave":false}""", all.GetProperty("state").GetRawText());
            Assert.AreEqual(1, all.GetProperty("views").GetArrayLength(), "views themselves survive");
            Assert.IsNotNull(token);
        }

        private static async Task<string[]> TrackedViewsAsync(HoistTestHost host)
        {
            await host.Services.GetRequiredService<TrackService>().FlushAsync();
            return (await host.Services.GetRequiredService<ITrackLogRepository>().ListAllAsync()).Where(t => t.Category == "Views").Select(t => t.Msg).ToArray();
        }
    }
}
