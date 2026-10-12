using System.Text.Json;
using Hoist.Tests.Support;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/auth.md - authStatus, login, logout, authConfig, getIdentity and the auth filter.</summary>
    [TestClass]
    public class TestAuthEndpoints
    {
        private static FormUrlEncodedContent Form(params (string k, string v)[] values) =>
            new FormUrlEncodedContent(values.ToDictionary(x => x.k, x => x.v));

        [TestMethod]
        public async Task TestAuthStatusWithoutCredentialsIs401WithNoBody()
        {
            await using var host = await HoistTestHost.StartAsync();
            var response = await host.Client.GetAsync("xh/authStatus");
            Assert.AreEqual(401, (int)response.StatusCode);
            Assert.AreEqual("", await response.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task TestProtectedEndpointWithoutCredentialsIs401()
        {
            await using var host = await HoistTestHost.StartAsync();
            var response = await host.Client.GetAsync("xh/getConfig");
            Assert.AreEqual(401, (int)response.StatusCode);
            Assert.AreEqual("", await response.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task TestInvalidOrExpiredTokenIs401()
        {
            await using var host = await HoistTestHost.StartAsync();
            var otherKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("a-completely-different-signing-key-0123456789abcdef"));

            host.SetBearer(TestTokens.Create("alice", signingKey: otherKey));
            Assert.AreEqual(401, (int)(await host.Client.GetAsync("xh/authStatus")).StatusCode);

            host.SetBearer(TestTokens.Create("alice", expires: DateTime.UtcNow.AddHours(-1)));
            Assert.AreEqual(401, (int)(await host.Client.GetAsync("xh/authStatus")).StatusCode);
        }

        [TestMethod]
        public async Task TestAuthStatusWithBearerAuthenticates()
        {
            await using var host = await HoistTestHost.StartAsync();
            host.SetBearer(TestTokens.Create("Alice", "alice@test.com", "Alice Smith"));

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/authStatus"));
            JsonAssert.HasExactKeys(json, "authenticated", "identity");
            Assert.IsTrue(json.GetProperty("authenticated").GetBoolean());

            var identity = json.GetProperty("identity");
            JsonAssert.HasExactKeys(identity, "user", "roles");
            var user = identity.GetProperty("user");
            JsonAssert.HasExactKeys(user, "username", "email", "displayName", "active");
            Assert.AreEqual("alice", user.GetProperty("username").GetString());
            Assert.AreEqual("alice@test.com", user.GetProperty("email").GetString());
            Assert.AreEqual("Alice Smith", user.GetProperty("displayName").GetString());
            Assert.IsTrue(user.GetProperty("active").GetBoolean());
            Assert.AreEqual(JsonValueKind.Array, identity.GetProperty("roles").ValueKind);
        }

        [TestMethod]
        public async Task TestSessionCookieKeepsUserAuthenticated()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("bob");
            host.SetBearer(null);

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/authStatus"));
            Assert.AreEqual("bob", json.GetProperty("identity").GetProperty("user").GetProperty("username").GetString());
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getConfig"));

            // A different browser has no session.
            var other = host.NewClient();
            Assert.AreEqual(401, (int)(await other.GetAsync("xh/getConfig")).StatusCode);
        }

        [TestMethod]
        public async Task TestLoginRequiresMatchingBearer()
        {
            await using var host = await HoistTestHost.StartAsync();

            // No token: login fails without error.
            var noToken = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/login", Form(("username", "carol"), ("password", "x"))));
            JsonAssert.HasExactKeys(noToken, "success", "identity");
            Assert.IsFalse(noToken.GetProperty("success").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, noToken.GetProperty("identity").ValueKind);

            // Token for someone else: login fails.
            host.SetBearer(TestTokens.Create("dave"));
            var mismatch = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/login", Form(("username", "carol"), ("password", "x"))));
            Assert.IsFalse(mismatch.GetProperty("success").GetBoolean());

            // Matching token: success, identity returned.
            host.SetBearer(TestTokens.Create("carol"));
            var ok = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/login", Form(("username", "carol"), ("password", "*****"))));
            Assert.IsTrue(ok.GetProperty("success").GetBoolean());
            Assert.AreEqual("carol", ok.GetProperty("identity").GetProperty("user").GetProperty("username").GetString());
        }

        [TestMethod]
        public async Task TestGetIdentity()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("erin");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getIdentity"));
            JsonAssert.HasExactKeys(json, "user", "roles");
        }

        [TestMethod]
        public async Task TestLogoutClearsSession()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("frank");
            host.SetBearer(null);

            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/logout"));
            JsonAssert.HasExactKeys(json, "success");
            Assert.IsTrue(json.GetProperty("success").GetBoolean());
            Assert.AreEqual(401, (int)(await host.Client.GetAsync("xh/authStatus")).StatusCode);
        }

        [TestMethod]
        public async Task TestAuthConfigIsWhitelistedAndEmptyByDefault()
        {
            await using var host = await HoistTestHost.StartAsync();
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/authConfig"));
            Assert.AreEqual("{}", json.GetRawText());
        }

        [TestMethod]
        public async Task TestBootstrapAdminUserGetsAdminRolesInDevelopment()
        {
            await using var host = await HoistTestHost.StartAsync(o => o.BootstrapAdminUser = "Root");
            await host.LoginAsync("root");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getIdentity"));
            var roles = json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
            CollectionAssert.IsSubsetOf(new[] { "HOIST_ADMIN", "HOIST_ADMIN_READER", "HOIST_ROLE_MANAGER" }, roles);
        }

        [TestMethod]
        public async Task TestInactiveUserIsForbidden()
        {
            await using var host = await HoistTestHost.StartAsync(configureServices: s =>
                s.AddSingletonUserService(new FakeUserService().Add("ghost", active: false)));
            host.SetBearer(TestTokens.Create("ghost"));
            var response = await host.Client.GetAsync("xh/authStatus");
            Assert.AreEqual(403, (int)response.StatusCode);
            Assert.AreEqual("", await response.Content.ReadAsStringAsync());
        }
    }
}
