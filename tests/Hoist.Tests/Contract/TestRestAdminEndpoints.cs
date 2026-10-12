using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Hoist.Core.Interfaces;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/admin.md - RestController endpoints for configs, prefs, user prefs and JSON blobs.</summary>
    [TestClass]
    public class TestRestAdminEndpoints
    {
        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        /// <summary>admin is a HOIST_ADMIN in the DB; reader holds HOIST_ADMIN_READER only; bob holds nothing.</summary>
        private static async Task<HoistTestHost> StartAsync()
        {
            var host = await HoistTestHost.StartAsync();
            var roles = host.Services.GetRequiredService<DefaultRoleService>();
            await roles.AssignRoleAsync("admin", "HOIST_ADMIN");
            await roles.AssignRoleAsync("reader", "HOIST_ADMIN_READER");
            await host.LoginAsync("admin");
            return host;
        }

        [TestMethod]
        public async Task TestConfigAdminReadShapeAndLookups()
        {
            await using var host = await StartAsync();
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("rest/configAdmin"));
            JsonAssert.HasExactKeys(json, "data");
            var rows = json.GetProperty("data").EnumerateArray().ToList();
            Assert.AreEqual(40, rows.Count);

            var tracking = rows.Single(r => r.GetProperty("name").GetString() == "xhActivityTrackingConfig");
            JsonAssert.HasExactKeys(tracking, "id", "name", "groupName", "valueType", "value", "overrideValue", "resolvedValue", "defaultValue", "clientVisible", "note", "lastUpdatedBy", "lastUpdated");
            Assert.AreEqual(JsonValueKind.String, tracking.GetProperty("value").ValueKind, "json values are raw strings in the admin format");
            Assert.AreEqual(2000, tracking.GetProperty("resolvedValue").GetProperty("maxDataLength").GetInt32());
            Assert.AreEqual(2000, tracking.GetProperty("defaultValue").GetProperty("maxDataLength").GetInt32());

            var impersonation = rows.Single(r => r.GetProperty("name").GetString() == "xhEnableImpersonation");
            Assert.AreEqual(JsonValueKind.False, impersonation.GetProperty("value").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, impersonation.GetProperty("resolvedValue").ValueKind);

            var lookups = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("rest/configAdmin/lookupData"));
            JsonAssert.HasExactKeys(lookups, "valueTypes", "groupNames");
            Assert.AreEqual("""["string","int","long","double","bool","json","pwd"]""", lookups.GetProperty("valueTypes").GetRawText());
            Assert.AreEqual("""["xh.io"]""", lookups.GetProperty("groupNames").GetRawText());
        }

        [TestMethod]
        public async Task TestConfigAdminCreateUpdateDeleteAreAudited()
        {
            await using var host = await StartAsync();

            var created = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("rest/configAdmin",
                Json("""{"data":{"name":"appFlag","groupName":"App","valueType":"bool","value":"true","clientVisible":true,"note":"n"}}""")));
            var row = created.GetProperty("data");
            Assert.AreEqual("admin", row.GetProperty("lastUpdatedBy").GetString());
            Assert.IsTrue(row.GetProperty("value").GetBoolean());
            var id = row.GetProperty("id").GetInt64();

            var updated = await JsonAssert.ReadJsonAsync(await host.Client.PutAsync($"rest/configAdmin/{id}",
                Json($$$"""{"data":{"id":{{{id}}},"name":"appFlag","groupName":"App","valueType":"bool","value":"false","clientVisible":true,"note":"n2"}}""")));
            Assert.IsFalse(updated.GetProperty("data").GetProperty("value").GetBoolean());
            Assert.AreEqual("n2", updated.GetProperty("data").GetProperty("note").GetString());

            var single = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync($"rest/configAdmin/{id}"));
            Assert.AreEqual(1, single.GetProperty("data").GetArrayLength());

            var invalid = await JsonAssert.ReadJsonAsync(await host.Client.PutAsync($"rest/configAdmin/{id}",
                Json($$$"""{"data":{"id":{{{id}}},"valueType":"bool","value":"maybe"}}""")), 400);
            Assert.AreEqual("ValidationException", invalid.GetProperty("name").GetString());

            await JsonAssert.NoContentAsync(await host.Client.DeleteAsync($"rest/configAdmin/{id}"));
            Assert.IsNull(await host.Services.GetRequiredService<IConfigRepository>().FindByIdAsync(id));

            await host.Services.GetRequiredService<TrackService>().FlushAsync();
            var audit = (await host.Services.GetRequiredService<ITrackLogRepository>().ListAllAsync()).Where(t => t.Category == "Audit").Select(t => t.Msg).ToArray();
            CollectionAssert.AreEqual(new[] { "Created AppConfig", "Updated AppConfig", "Deleted AppConfig" }, audit);
        }

        [TestMethod]
        public async Task TestPwdConfigShowsDigestAndKeepsSecretWhenDigestResubmitted()
        {
            await using var host = await StartAsync();
            var created = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("rest/configAdmin",
                Json("""{"data":{"name":"apiKey","groupName":"App","valueType":"pwd","value":"secret","clientVisible":false}}""")));
            var row = created.GetProperty("data");
            Assert.AreEqual("Xr4ilOzQ4PCOq3aQ0qbuaQ==", row.GetProperty("value").GetString(), "pwd values are shown as their digest");
            var id = row.GetProperty("id").GetInt64();

            // The admin form posts back every editable field, including the digest.
            await JsonAssert.ReadJsonAsync(await host.Client.PutAsync($"rest/configAdmin/{id}",
                Json($$$"""{"data":{"id":{{{id}}},"name":"apiKey","groupName":"App2","valueType":"pwd","value":"Xr4ilOzQ4PCOq3aQ0qbuaQ==","clientVisible":false}}""")));
            Assert.AreEqual("secret", await host.Services.GetRequiredService<ConfigService>().GetPwdAsync("apiKey"));
        }

        [TestMethod]
        public async Task TestBulkUpdateAndBulkDelete()
        {
            await using var host = await StartAsync();
            var ids = new List<long>();
            foreach (var n in new[] { "a1", "a2", "a3" })
            {
                var c = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("rest/configAdmin",
                    Json($$$"""{"data":{"name":"{{{n}}}","groupName":"Old","valueType":"string","value":"x"}}""")));
                ids.Add(c.GetProperty("data").GetProperty("id").GetInt64());
            }

            var bulk = await JsonAssert.ReadJsonAsync(await host.Client.PutAsync("rest/configAdmin/bulkUpdate",
                Json($$$"""{"ids":[{{{ids[0]}}},{{{ids[1]}}},999999],"newParams":{"groupName":"New"}}""")));
            Assert.AreEqual("""{"success":2,"fail":1}""", bulk.GetRawText());

            var del = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync($"rest/configAdmin/bulkDelete?ids={ids[0]}&ids={ids[1]}&ids=999999", null));
            Assert.AreEqual("""{"success":2,"fail":1}""", del.GetRawText());
        }

        [TestMethod]
        public async Task TestAccessControl()
        {
            await using var host = await StartAsync();

            var reader = host.NewClient();
            await host.LoginAsync("reader", reader);
            await JsonAssert.ReadJsonAsync(await reader.GetAsync("rest/configAdmin"));
            var denied = await JsonAssert.ReadJsonAsync(await reader.PostAsync("rest/configAdmin", Json("""{"data":{"name":"x","valueType":"string","value":"x"}}""")), 403);
            Assert.AreEqual("You do not have the required role(s) for this action. Currently logged in as: reader.", denied.GetProperty("message").GetString());

            var bob = host.NewClient();
            await host.LoginAsync("bob", bob);
            await JsonAssert.ReadJsonAsync(await bob.GetAsync("rest/configAdmin"), 403);
            await JsonAssert.ReadJsonAsync(await bob.GetAsync("rest/configAdmin/lookupData"), 403);
        }

        [TestMethod]
        public async Task TestPreferenceAdmin()
        {
            await using var host = await StartAsync();
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("rest/preferenceAdmin"));
            var theme = json.GetProperty("data").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "xhTheme");
            JsonAssert.HasExactKeys(theme, "id", "name", "groupName", "type", "defaultValue", "notes", "lastUpdatedBy", "lastUpdated");

            var sizing = json.GetProperty("data").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "xhSizingMode");
            Assert.AreEqual(JsonValueKind.String, sizing.GetProperty("defaultValue").ValueKind, "json defaults stay strings in the admin format");

            var lookups = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("rest/preferenceAdmin/lookupData"));
            JsonAssert.HasExactKeys(lookups, "types", "groupNames");

            var created = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("rest/preferenceAdmin",
                Json("""{"data":{"name":"appPageSize","groupName":"App","type":"int","defaultValue":"50","notes":"rows"}}""")));
            Assert.AreEqual(50, created.GetProperty("data").GetProperty("defaultValue").GetInt32());
        }

        [TestMethod]
        public async Task TestUserPreferenceAdmin()
        {
            await using var host = await StartAsync();
            var created = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("rest/userPreferenceAdmin",
                Json("""{"data":{"name":"xhTheme","username":"bob","userValue":"dark"}}""")));
            var row = created.GetProperty("data");
            JsonAssert.HasExactKeys(row, "id", "name", "groupName", "type", "username", "userValue", "lastUpdatedBy", "lastUpdated");
            Assert.AreEqual("xhTheme", row.GetProperty("name").GetString());
            Assert.AreEqual("admin", row.GetProperty("lastUpdatedBy").GetString());

            await host.Client.PostAsync("rest/userPreferenceAdmin", Json("""{"data":{"name":"xhShowVersionBar","username":"carol","userValue":"never"}}"""));

            var byUser = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("rest/userPreferenceAdmin?query=" + Uri.EscapeDataString("""{"username":"bo"}""")));
            Assert.AreEqual(1, byUser.GetProperty("data").GetArrayLength());
            var byName = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("rest/userPreferenceAdmin?query=" + Uri.EscapeDataString("""{"name":"xhShowVersionBar"}""")));
            Assert.AreEqual("carol", byName.GetProperty("data")[0].GetProperty("username").GetString());

            var lookups = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("rest/userPreferenceAdmin/lookupData"));
            JsonAssert.HasExactKeys(lookups, "names");
            Assert.AreEqual(6, lookups.GetProperty("names").GetArrayLength());
        }

        [TestMethod]
        public async Task TestJsonBlobAdmin()
        {
            await using var host = await StartAsync();
            var created = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("rest/jsonBlobAdmin",
                Json("""{"data":{"type":"grid","name":"B1","owner":"bob","acl":null,"value":"{\"a\":1}","meta":null,"description":"d"}}""")));
            var row = created.GetProperty("data");
            JsonAssert.HasExactKeys(row, "id", "token", "type", "owner", "acl", "name", "value", "meta", "description", "archived", "archivedDate", "dateCreated", "lastUpdated", "lastUpdatedBy");
            Assert.AreEqual("""{"a":1}""", row.GetProperty("value").GetString());
            var id = row.GetProperty("id").GetInt64();

            var archived = await JsonAssert.ReadJsonAsync(await host.Client.PutAsync($"rest/jsonBlobAdmin/{id}", Json($$$"""{"data":{"id":{{{id}}},"archived":true}}""")));
            Assert.IsTrue(archived.GetProperty("data").GetProperty("archived").GetBoolean());
            var restored = await JsonAssert.ReadJsonAsync(await host.Client.PutAsync($"rest/jsonBlobAdmin/{id}", Json($$$"""{"data":{"id":{{{id}}},"archived":false}}""")));
            Assert.AreEqual(0, restored.GetProperty("data").GetProperty("archivedDate").GetInt64());

            var lookups = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("rest/jsonBlobAdmin/lookupData"));
            Assert.AreEqual("""{"types":["grid"]}""", lookups.GetRawText());
        }
    }
}
