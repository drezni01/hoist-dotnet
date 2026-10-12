using System.Text;
using System.Text.Json;
using Hoist.Core.Interfaces;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/admin.md - roleAdmin/*, userAdmin/*, alertBannerAdmin/* and unported admin endpoints.</summary>
    [TestClass]
    public class TestRoleUserAdminEndpoints
    {
        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static async Task<HoistTestHost> StartAsync()
        {
            var host = await HoistTestHost.StartAsync();
            var roles = host.Services.GetRequiredService<DefaultRoleService>();
            await roles.AssignRoleAsync("admin", "HOIST_ADMIN");
            await roles.AssignRoleAsync("admin", "HOIST_ROLE_MANAGER");
            await host.LoginAsync("admin");
            return host;
        }

        [TestMethod]
        public async Task TestRoleConfig()
        {
            await using var host = await StartAsync();
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("roleAdmin/config"));
            Assert.AreEqual("""{"enabled":true,"userAssignmentSupported":true,"directoryGroupsSupported":false,"directoryGroupsDescription":null}""", json.GetRawText());
        }

        [TestMethod]
        public async Task TestRoleCrudAndList()
        {
            await using var host = await StartAsync();

            var created = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("roleAdmin/create",
                Json("""{"name":"APP_READER","category":"App","notes":"Readers","users":["Bob"],"directoryGroups":[],"roles":["APP_EDITOR"]}""")));
            JsonAssert.HasExactKeys(created.GetProperty("data"), "name", "category", "notes", "lastUpdated", "lastUpdatedBy");
            await host.Client.PostAsync("roleAdmin/create", Json("""{"name":"APP_EDITOR","category":"App","notes":null,"users":["carol"],"directoryGroups":[],"roles":[]}"""));

            var list = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("roleAdmin/list"));
            var reader = list.GetProperty("data").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "APP_READER");
            JsonAssert.HasExactKeys(reader, "name", "category", "notes", "members", "lastUpdated", "lastUpdatedBy", "inheritedRoles", "effectiveRoles", "effectiveDirectoryGroups", "effectiveUsers", "errors");
            var members = reader.GetProperty("members").EnumerateArray().ToList();
            Assert.AreEqual(2, members.Count);
            JsonAssert.HasExactKeys(members[0], "type", "name", "dateCreated", "createdBy");
            Assert.IsTrue(members.Any(m => m.GetProperty("type").GetString() == "USER" && m.GetProperty("name").GetString() == "bob"));

            Assert.AreEqual("""[{"name":"APP_EDITOR","sourceRoles":["APP_READER"]}]""", reader.GetProperty("effectiveRoles").GetRawText());
            var users = reader.GetProperty("effectiveUsers").EnumerateArray().ToDictionary(u => u.GetProperty("name").GetString(), u => u.GetProperty("sources").GetRawText());
            Assert.AreEqual("""[{"role":"APP_READER"}]""", users["bob"]);
            Assert.AreEqual("""[{"role":"APP_EDITOR"}]""", users["carol"]);
            Assert.AreEqual("""{"directoryGroups":{}}""", reader.GetProperty("errors").GetRawText());

            var editor = list.GetProperty("data").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "APP_EDITOR");
            Assert.AreEqual("""[{"name":"APP_READER","sourceRoles":["APP_EDITOR"]}]""", editor.GetProperty("inheritedRoles").GetRawText());

            // Role assignments take effect immediately.
            var carolRoles = await host.Services.GetRequiredService<IRoleService>().GetRolesForUserAsync("carol");
            CollectionAssert.AreEquivalent(new[] { "APP_EDITOR", "APP_READER" }, carolRoles.ToArray());

            var updated = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("roleAdmin/update",
                Json("""{"name":"APP_READER","category":"App2","notes":"Readers","users":["dave"],"directoryGroups":[],"roles":[]}""")));
            Assert.AreEqual("App2", updated.GetProperty("data").GetProperty("category").GetString());
            Assert.AreEqual(0, (await host.Services.GetRequiredService<IRoleService>().GetRolesForUserAsync("bob")).Count);

            var bulk = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("roleAdmin/bulkCategoryUpdate", Json("""{"roles":["APP_READER","APP_EDITOR"],"category":"Bulk"}""")));
            Assert.AreEqual(2, bulk.GetProperty("data").GetArrayLength());

            await JsonAssert.NoContentAsync(await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "roleAdmin/delete") { Content = Json("""{"name":"APP_EDITOR"}""") }));
            var roles = await host.Services.GetRequiredService<IRoleRepository>().ListAsync();
            Assert.IsFalse(roles.Any(r => r.Name == "APP_EDITOR"));

            await host.Services.GetRequiredService<TrackService>().FlushAsync();
            var audit = (await host.Services.GetRequiredService<ITrackLogRepository>().ListAllAsync()).Where(t => t.Category == "Audit").Select(t => t.Msg).ToList();
            CollectionAssert.IsSubsetOf(new[] { "Created role: 'APP_READER'", "Edited role: 'APP_READER'", "Updated 2 roles.", "Deleted role: 'APP_EDITOR'" }, audit);
        }

        [TestMethod]
        public async Task TestRoleNameMustBeCaseInsensitiveUnique()
        {
            await using var host = await StartAsync();
            var json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("roleAdmin/create",
                Json("""{"name":"hoist_admin","category":null,"notes":null,"users":[],"directoryGroups":[],"roles":[]}""")), 400);
            Assert.AreEqual("Role Name must be case-insensitive unique.", json.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task TestRoleManagerRequired()
        {
            await using var host = await StartAsync();
            await host.Services.GetRequiredService<DefaultRoleService>().AssignRoleAsync("reader", "HOIST_ADMIN_READER");
            var reader = host.NewClient();
            await host.LoginAsync("reader", reader);
            await JsonAssert.ReadJsonAsync(await reader.GetAsync("roleAdmin/list"));
            await JsonAssert.ReadJsonAsync(await reader.PostAsync("roleAdmin/create", Json("""{"name":"X","users":[],"directoryGroups":[],"roles":[]}""")), 403);
        }

        [TestMethod]
        public async Task TestDirectoryGroupEndpointsReportUnsupported()
        {
            await using var host = await StartAsync();
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("roleAdmin/searchDirectoryGroups?query=x"), 404);
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("roleAdmin/directoryGroupsInfo?names=x"), 404);
        }

        [TestMethod]
        public async Task TestUserAdmin()
        {
            await using var host = await StartAsync();
            var users = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("userAdmin/users?activeOnly=true"));
            var admin = users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "admin");
            JsonAssert.HasExactKeys(admin, "username", "email", "displayName", "active");
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("userAdmin/users?activeOnly=yes"), 500);

            var roles = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("userAdmin/roles"));
            Assert.AreEqual(JsonValueKind.Array, roles.GetProperty("HOIST_ADMIN").ValueKind);

            var forUser = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("userAdmin/rolesForUser?user=admin"));
            JsonAssert.HasExactKeys(forUser, "user", "roles");
            var forRole = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("userAdmin/usersForRole?role=HOIST_ADMIN"));
            Assert.AreEqual("""{"role":"HOIST_ADMIN","users":["admin"]}""", forRole.GetRawText());
        }

        [TestMethod]
        public async Task TestAlertBannerAdmin()
        {
            await using var host = await StartAsync();
            Assert.AreEqual("""{"active":false}""", (await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("alertBannerAdmin/alertSpec"))).GetRawText());
            Assert.AreEqual("[]", (await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("alertBannerAdmin/alertPresets"))).GetRawText());

            await JsonAssert.NoContentAsync(await host.Client.PostAsync("alertBannerAdmin/setAlertSpec", Json("""{"active":true,"message":"Hello","intent":"primary"}""")));
            await JsonAssert.NoContentAsync(await host.Client.PostAsync("alertBannerAdmin/setAlertPresets", Json("""[{"message":"Preset"}]""")));

            var spec = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("alertBannerAdmin/alertSpec"));
            Assert.AreEqual("Hello", spec.GetProperty("message").GetString());
            Assert.AreEqual(1, (await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("alertBannerAdmin/alertPresets"))).GetArrayLength());

            var env = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/environmentPoll"));
            Assert.AreEqual("Hello", env.GetProperty("alertBanner").GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task TestUnportedAdminEndpointsAre404()
        {
            await using var host = await StartAsync();
            foreach (var url in new[] { "clusterAdmin/allInstances", "logViewerAdmin/listFiles", "rest/monitorAdmin", "metricsAdmin/listMetrics", "clientAdmin/allClients" })
            {
                var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync(url), 404);
                Assert.AreEqual("NotFoundException", json.GetProperty("name").GetString(), url);
            }
        }
    }
}
