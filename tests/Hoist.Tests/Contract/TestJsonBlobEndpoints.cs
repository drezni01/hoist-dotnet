using System.Text.Json;
using Hoist.Tests.Support;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/jsonblob.md - xh/*JsonBlob endpoints and v42 access rules.</summary>
    [TestClass]
    public class TestJsonBlobEndpoints
    {
        public static readonly string[] ClientKeys =
        {
            "id", "token", "type", "owner", "acl", "name", "archived", "archivedDate", "description", "dateCreated", "lastUpdated", "lastUpdatedBy", "meta", "value"
        };

        private static FormUrlEncodedContent Form(params (string k, string v)[] values) =>
            new FormUrlEncodedContent(values.ToDictionary(x => x.k, x => x.v));

        private static async Task<JsonElement> CreateAsync(HttpClient client, string data, int status = 200) =>
            await JsonAssert.ReadJsonAsync(await client.PostAsync("xh/createJsonBlob", Form(("data", data))), status);

        [TestMethod]
        public async Task TestCreateAndGet()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            var blob = await CreateAsync(host.Client, """{"type":"grid","name":"My Blob","value":{"cols":[1,2]},"meta":{"group":"g"},"description":"desc"}""");
            JsonAssert.HasExactKeys(blob, ClientKeys);
            Assert.AreEqual(8, blob.GetProperty("token").GetString().Length);
            Assert.AreEqual("alice", blob.GetProperty("owner").GetString());
            Assert.AreEqual(JsonValueKind.Null, blob.GetProperty("acl").ValueKind);
            Assert.IsFalse(blob.GetProperty("archived").GetBoolean());
            Assert.AreEqual(0, blob.GetProperty("archivedDate").GetInt64());
            Assert.AreEqual(JsonValueKind.Number, blob.GetProperty("dateCreated").ValueKind);
            Assert.AreEqual("alice", blob.GetProperty("lastUpdatedBy").GetString());
            Assert.AreEqual(2, blob.GetProperty("value").GetProperty("cols").GetArrayLength());
            Assert.AreEqual("g", blob.GetProperty("meta").GetProperty("group").GetString());

            var token = blob.GetProperty("token").GetString();
            var got = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/getJsonBlob", Form(("token", token))));
            Assert.AreEqual("My Blob", got.GetProperty("name").GetString());
        }

        [TestMethod]
        public async Task TestReadAccessRequiresOwnerOrPublicAcl()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var priv = (await CreateAsync(host.Client, """{"type":"grid","name":"private","value":{}}""")).GetProperty("token").GetString();
            var shared = (await CreateAsync(host.Client, """{"type":"grid","name":"shared","value":{},"acl":"*"}""")).GetProperty("token").GetString();

            var bob = host.NewClient();
            await host.LoginAsync("bob", bob);
            var denied = await JsonAssert.ReadJsonAsync(await bob.PostAsync("xh/getJsonBlob", Form(("token", priv))), 403);
            Assert.AreEqual("NotAuthorizedException", denied.GetProperty("name").GetString());
            Assert.AreEqual($"User 'bob' does not have access to JsonBlob with token '{priv}'", denied.GetProperty("message").GetString());

            await JsonAssert.ReadJsonAsync(await bob.PostAsync("xh/getJsonBlob", Form(("token", shared))));

            var list = await JsonAssert.ReadJsonAsync(await bob.PostAsync("xh/listJsonBlobs", Form(("type", "grid"), ("includeValue", "false"))));
            Assert.AreEqual(1, list.GetArrayLength());
            Assert.IsFalse(list[0].TryGetProperty("value", out _));
        }

        [TestMethod]
        public async Task TestOnlyOwnerCanWrite()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var token = (await CreateAsync(host.Client, """{"type":"grid","name":"shared","value":{"a":1},"acl":"*"}""")).GetProperty("token").GetString();

            var bob = host.NewClient();
            await host.LoginAsync("bob", bob);
            var denied = await JsonAssert.ReadJsonAsync(await bob.PostAsync("xh/updateJsonBlob", Form(("token", token), ("update", """{"value":{"a":2}}"""))), 403);
            Assert.AreEqual("User 'bob' does not have write access to JsonBlobs owned by 'alice'", denied.GetProperty("message").GetString());

            var updated = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/updateJsonBlob", Form(("token", token), ("update", """{"value":{"a":2},"type":"changed"}"""))));
            Assert.AreEqual(2, updated.GetProperty("value").GetProperty("a").GetInt32());
            Assert.AreEqual("grid", updated.GetProperty("type").GetString(), "type is fixed at creation");
        }

        [TestMethod]
        public async Task TestGlobalBlobsNeedGlobalWriteRole()
        {
            await using var host = await HoistTestHost.StartAsync(o => o.BootstrapAdminUser = "admin");
            await host.LoginAsync("alice");
            var denied = await CreateAsync(host.Client, """{"type":"grid","name":"global","value":{},"owner":null,"acl":"*"}""", 403);
            Assert.AreEqual("User 'alice' does not have write access to global JsonBlobs of type 'grid'", denied.GetProperty("message").GetString());

            var admin = host.NewClient();
            await host.LoginAsync("admin", admin);
            var created = await CreateAsync(admin, """{"type":"grid","name":"global","value":{},"owner":null,"acl":"*"}""");
            Assert.AreEqual(JsonValueKind.Null, created.GetProperty("owner").ValueKind);
        }

        [TestMethod]
        public async Task TestNameMustBeUniquePerOwnerAndType()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            await CreateAsync(host.Client, """{"type":"grid","name":"dup","value":{}}""");
            var json = await CreateAsync(host.Client, """{"type":"grid","name":"dup","value":{}}""", 400);
            Assert.AreEqual("ValidationException", json.GetProperty("name").GetString());
            await CreateAsync(host.Client, """{"type":"other","name":"dup","value":{}}""");
        }

        [TestMethod]
        public async Task TestCreateOrUpdateFindAndArchive()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");

            var first = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/createOrUpdateJsonBlob", Form(("type", "t"), ("name", "n"), ("update", """{"value":{"v":1}}"""))));
            var second = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/createOrUpdateJsonBlob", Form(("type", "t"), ("name", "n"), ("update", """{"value":{"v":2}}"""))));
            Assert.AreEqual(first.GetProperty("token").GetString(), second.GetProperty("token").GetString());
            Assert.AreEqual(2, second.GetProperty("value").GetProperty("v").GetInt32());

            var found = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/findJsonBlob", Form(("type", "t"), ("name", "n"), ("owner", "alice"))));
            Assert.AreEqual(first.GetProperty("token").GetString(), found.GetProperty("token").GetString());
            var none = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/findJsonBlob", Form(("type", "t"), ("name", "missing"))));
            Assert.AreEqual(JsonValueKind.Null, none.ValueKind);

            var token = first.GetProperty("token").GetString();
            var archived = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/archiveJsonBlob", Form(("token", token))));
            Assert.IsTrue(archived.GetProperty("archived").GetBoolean());
            Assert.IsTrue(archived.GetProperty("archivedDate").GetInt64() > 0);

            var gone = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/getJsonBlob", Form(("token", token))), 500);
            Assert.AreEqual($"Active JsonBlob not found with token '{token}'", gone.GetProperty("message").GetString());
        }
    }
}
