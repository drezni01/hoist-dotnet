using System.Text.Json;
using Hoist.Core.Interfaces;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Contract
{
    /// <summary>Contract: docs/api/auth.md (impersonation) and docs/api/core.md (misc) - impersonation, getTimeZoneOffset, echoHeaders.</summary>
    [TestClass]
    public class TestImpersonationEndpoints
    {
        private static FormUrlEncodedContent Form(params (string k, string v)[] values) =>
            new FormUrlEncodedContent(values.ToDictionary(x => x.k, x => x.v));

        /// <summary>
        /// admin and carol are HOIST_ADMIN members in the database (so also HOIST_IMPERSONATOR, by role inheritance);
        /// bob is a plain user. A bootstrap admin would not do: its roles are not inherited.
        /// </summary>
        private static async Task<HoistTestHost> StartAsync(bool enabled = true)
        {
            var host = await HoistTestHost.StartAsync();
            var roles = host.Services.GetRequiredService<DefaultRoleService>();
            await roles.AssignRoleAsync("admin", "HOIST_ADMIN");
            await roles.AssignRoleAsync("carol", "HOIST_ADMIN");
            await host.LoginAsync("bob", host.NewClient());
            if (enabled) await host.Services.GetRequiredService<ConfigService>().SetValueAsync("xhEnableImpersonation", "true", "test");
            await host.LoginAsync("admin");
            return host;
        }

        [TestMethod]
        public async Task TestImpersonationTargets()
        {
            await using var host = await StartAsync();
            var json = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/impersonationTargets"));
            var names = json.EnumerateArray().Select(t => { JsonAssert.HasExactKeys(t, "username"); return t.GetProperty("username").GetString(); }).ToList();
            CollectionAssert.IsSubsetOf(new[] { "admin", "bob", "carol" }, names);

            var bob = host.NewClient();
            await host.LoginAsync("bob", bob);
            Assert.AreEqual("[]", (await JsonAssert.ReadJsonAsync(await bob.GetAsync("xh/impersonationTargets"))).GetRawText());
        }

        [TestMethod]
        public async Task TestImpersonateAndEnd()
        {
            await using var host = await StartAsync();
            await JsonAssert.NoContentAsync(await host.Client.PostAsync("xh/impersonate", Form(("username", "bob"))));

            var identity = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getIdentity"));
            JsonAssert.HasExactKeys(identity, "apparentUser", "apparentUserRoles", "authUser", "authUserRoles");
            Assert.AreEqual("bob", identity.GetProperty("apparentUser").GetProperty("username").GetString());
            Assert.AreEqual("admin", identity.GetProperty("authUser").GetProperty("username").GetString());
            Assert.AreEqual(0, identity.GetProperty("apparentUserRoles").GetArrayLength());

            // User-specific calls now act as the apparent user.
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getPrefs?clientUsername=bob"));
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getPrefs?clientUsername=admin"), 400);

            await JsonAssert.NoContentAsync(await host.Client.PostAsync("xh/endImpersonate", null));
            identity = await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/getIdentity"));
            JsonAssert.HasExactKeys(identity, "user", "roles");
            Assert.AreEqual("admin", identity.GetProperty("user").GetProperty("username").GetString());

            await host.Services.GetRequiredService<TrackService>().FlushAsync();
            var tracked = (await host.Services.GetRequiredService<ITrackLogRepository>().ListAllAsync()).Where(t => t.Category == "Impersonate").ToList();
            CollectionAssert.AreEqual(new[] { "Started impersonation", "Stopped impersonation" }, tracked.Select(t => t.Msg).ToArray());
            Assert.IsTrue(tracked.All(t => t.Severity == "WARN" && t.Username == "admin"));
            Assert.AreEqual("""{"target":"bob"}""", tracked[0].Data);
            Assert.AreEqual("bob", tracked[1].Impersonating);
        }

        [TestMethod]
        public async Task TestImpersonationFailures()
        {
            await using (var disabled = await StartAsync(enabled: false))
            {
                var json = await JsonAssert.ReadJsonAsync(await disabled.Client.PostAsync("xh/impersonate", Form(("username", "bob"))), 500);
                Assert.AreEqual("Impersonation is disabled for this app.", json.GetProperty("message").GetString());
            }

            await using var host = await StartAsync();
            var unknown = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/impersonate", Form(("username", "nobody"))), 500);
            Assert.AreEqual("User 'nobody' not found", unknown.GetProperty("message").GetString());

            var bob = host.NewClient();
            await host.LoginAsync("bob", bob);
            var notAllowed = await JsonAssert.ReadJsonAsync(await bob.PostAsync("xh/impersonate", Form(("username", "admin"))), 500);
            Assert.AreEqual("User not found or user not authorized for impersonation.", notAllowed.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task TestGetTimeZoneOffset()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/getTimeZoneOffset", Form(("timeZoneId", "Asia/Kolkata"))));
            Assert.AreEqual("""{"offset":19800000}""", json.GetRawText());

            var bad = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/getTimeZoneOffset", Form(("timeZoneId", "Mars/Olympus"))), 404);
            Assert.AreEqual("Unknown timeZoneId", bad.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task TestEchoHeaders()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.LoginAsync("alice");
            var request = new HttpRequestMessage(HttpMethod.Get, "xh/echoHeaders");
            request.Headers.Add("X-Test-Header", "hello");
            var json = await JsonAssert.ReadJsonAsync(await host.Client.SendAsync(request));
            Assert.AreEqual("hello", json.GetProperty("X-Test-Header").GetString());
        }
    }
}
