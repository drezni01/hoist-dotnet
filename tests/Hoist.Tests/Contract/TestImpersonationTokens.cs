using System.Net.Http.Headers;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>
    /// Impersonation tokens (docs/api/auth.md "Impersonation tokens"): the host app issues a
    /// token for the target user carrying an "impersonator" claim, so its own API acts as the target. When
    /// such a token establishes a Hoist session, the session is the impersonation (apparent = target,
    /// auth = impersonator) - re-checked against Hoist's impersonation rules - not the target's own.
    /// </summary>
    [TestClass]
    public class TestImpersonationTokens
    {
        /// <summary>admin is HOIST_ADMIN (so also an impersonator); bob is a plain user; carol an impersonator only.</summary>
        private static async Task<HoistTestHost> StartAsync(bool enabled = true)
        {
            var host = await HoistTestHost.StartAsync();
            var roles = host.Services.GetRequiredService<DefaultRoleService>();
            await roles.AssignRoleAsync("admin", "HOIST_ADMIN");
            await roles.AssignRoleAsync("carol", "HOIST_IMPERSONATOR");
            await host.LoginAsync("bob", host.NewClient());
            if (enabled) await host.Services.GetRequiredService<ConfigService>().SetValueAsync("xhEnableImpersonation", "true", "test");
            return host;
        }

        private static HttpClient WithToken(HoistTestHost host, string username, string impersonator)
        {
            var client = host.NewClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(username, impersonator: impersonator));
            return client;
        }

        [TestMethod]
        public async Task TestTokenEstablishesImpersonationSession()
        {
            await using var host = await StartAsync();
            var client = WithToken(host, "bob", impersonator: "admin");

            var identity = await JsonAssert.ReadJsonAsync(await client.GetAsync("xh/getIdentity"));
            JsonAssert.HasExactKeys(identity, "apparentUser", "apparentUserRoles", "authUser", "authUserRoles");
            Assert.AreEqual("bob", identity.GetProperty("apparentUser").GetProperty("username").GetString());
            Assert.AreEqual("admin", identity.GetProperty("authUser").GetProperty("username").GetString());

            // Prefs etc. act as the apparent user, as with xh/impersonate.
            await JsonAssert.ReadJsonAsync(await client.GetAsync("xh/getPrefs?clientUsername=bob"));
        }

        [TestMethod]
        public async Task TestLoginWithTokenKeepsImpersonation()
        {
            await using var host = await StartAsync();
            var client = WithToken(host, "bob", impersonator: "admin");

            var json = await JsonAssert.ReadJsonAsync(await client.PostAsync("xh/login",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "bob", ["password"] = "*****" })));
            Assert.IsTrue(json.GetProperty("success").GetBoolean());
            Assert.AreEqual("admin", json.GetProperty("identity").GetProperty("authUser").GetProperty("username").GetString());
        }

        [TestMethod]
        public async Task TestTokenRefusedWhenImpersonationDisabled()
        {
            await using var host = await StartAsync(enabled: false);
            Assert.AreEqual(401, (int)(await WithToken(host, "bob", impersonator: "admin").GetAsync("xh/getIdentity")).StatusCode);
        }

        [TestMethod]
        public async Task TestTokenRefusedForNonImpersonator()
        {
            await using var host = await StartAsync();
            await host.LoginAsync("dave", host.NewClient());   // a known user without impersonation rights
            Assert.AreEqual(401, (int)(await WithToken(host, "bob", impersonator: "dave").GetAsync("xh/getIdentity")).StatusCode);
        }

        [TestMethod]
        public async Task TestTokenRefusedForTargetOutsideRules()
        {
            // carol may impersonate, but not a Hoist admin.
            await using var host = await StartAsync();
            Assert.AreEqual(401, (int)(await WithToken(host, "admin", impersonator: "carol").GetAsync("xh/getIdentity")).StatusCode);
        }

        [TestMethod]
        public async Task TestCheckImpersonationForExplicitUser()
        {
            // The check a host runs before issuing an impersonation token - no Hoist session involved.
            await using var host = await StartAsync();
            var identity = host.Services.GetRequiredService<IdentityService>();

            var target = await identity.CheckImpersonationAsync(new HoistUser("admin"), "bob");
            Assert.AreEqual("bob", target.Username);

            var ex = await Assert.ThrowsExceptionAsync<Hoist.Core.Exceptions.HoistException>(() => identity.CheckImpersonationAsync(new HoistUser("carol"), "admin"));
            Assert.AreEqual("'carol' is not authorized to impersonate 'admin'", ex.Message);
        }
    }
}
