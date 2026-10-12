using Hoist.Api.Hosting;
using Hoist.Core.Exceptions;
using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Tests.Support;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hoist.Tests.Contract
{
    /// <summary>
    /// How a host app wires Hoist (docs/decisions/0004, docs/api/auth.md "Configuring authentication"):
    /// startup, the auth provider, the user directory, impersonation targets, and living alongside the
    /// host's own endpoints, filters and ports.
    /// </summary>
    [TestClass]
    public class TestHoistHosting
    {
        private static FormUrlEncodedContent Form(params (string k, string v)[] values) =>
            new FormUrlEncodedContent(values.ToDictionary(x => x.k, x => x.v));

        private static async Task<string> IdentityUsernameAsync(HttpClient client)
        {
            var json = await JsonAssert.ReadJsonAsync(await client.GetAsync("xh/getIdentity"));
            return json.TryGetProperty("user", out var user)
                ? user.GetProperty("username").GetString()
                : json.GetProperty("apparentUser").GetProperty("username").GetString();
        }

        /// <summary>A host with Hoist registered by registerHoist, and UseHoist/MapHoist in the pipeline.</summary>
        private static async Task<HoistTestHost> StartHostAsync(Action<IServiceCollection> registerHoist)
        {
            var builder = HoistTestHost.CreateBuilder();
            registerHoist(builder.Services);
            var app = builder.Build();
            app.UseHoist();
            app.MapHoist();
            return await HoistTestHost.StartAsync(app);
        }

        //------------------------
        // Startup
        //------------------------
        [TestMethod]
        public async Task TestInitializesBeforeAnyHostedServiceStarts()
        {
            // The probe is registered first, so it would start first without Hoist's lifecycle hook.
            var probe = new StartupProbe();
            await using var host = await StartHostAsync(s =>
            {
                s.AddSingleton<IHostedService>(sp => probe.With(sp));
                s.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
                s.AddHoistJwtAuth(jwt => jwt.SigningKey = TestTokens.SigningKey);
            });

            Assert.IsTrue(probe.HoistInitializedWhenStarted);
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/version"));
        }

        [TestMethod]
        public async Task TestExplicitInitializeIsHarmless()
        {
            await using var host = await HoistTestHost.StartAsync();
            await host.Services.InitializeHoistAsync();
            await host.Services.InitializeHoistAsync();
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/version"));
        }

        [TestMethod]
        public async Task TestSettingsAreReadAtStartupNotRegistration()
        {
            // As with user secrets added to configuration after AddHoist ran: the lambdas see the final values.
            var settings = new HostAuthConfig();
            await using var host = await StartHostAsync(s =>
            {
                s.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
                s.AddHoistJwtAuth(jwt => jwt.SigningKey = settings.SecretKey);
                settings.SecretKey = TestTokens.SigningKey;
            });

            await host.LoginAsync("alice");
            Assert.AreEqual("alice", await IdentityUsernameAsync(host.Client));
        }

        [TestMethod]
        public async Task TestClassicStartupHost()
        {
            // A classic host: Host.CreateDefaultBuilder + Startup, with the key in a host settings object
            // registered outside Startup (by the host's own bootstrap code).
            using var classic = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web => web
                    .UseTestServer()
                    .ConfigureServices(s => s.AddSingleton(new HostAuthConfig { SecretKey = TestTokens.SigningKey }))
                    .UseStartup<ClassicStartup>())
                .Build();
            await classic.StartAsync();
            await using var host = HoistTestHost.ForClient(classic.GetTestServer());

            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/version"));
            await host.LoginAsync("alice");
            Assert.AreEqual("alice", await IdentityUsernameAsync(host.Client));
            await classic.StopAsync();
        }

        //------------------------
        // Auth provider
        //------------------------
        [TestMethod]
        public async Task TestJwtAuthRequiresSigningKey()
        {
            var ex = await Assert.ThrowsExactlyAsync<HoistException>(() => StartHostAsync(s =>
            {
                s.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
                s.AddHoistJwtAuth(jwt => { });
            }));
            StringAssert.Contains(ex.Message, "SigningKey");
        }

        [TestMethod]
        public async Task TestJwtKeyFromHostServices()
        {
            await using var host = await StartHostAsync(s =>
            {
                s.AddSingleton(new HostAuthConfig { SecretKey = TestTokens.SigningKey });
                s.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
                s.AddHoistJwtAuth((jwt, sp) => jwt.SigningKey = sp.GetRequiredService<HostAuthConfig>().SecretKey);
            });

            await host.LoginAsync("alice");
            Assert.AreEqual("alice", await IdentityUsernameAsync(host.Client));
        }

        [TestMethod]
        public async Task TestJwtAuthRejectsTokenSignedWithOtherKey()
        {
            await using var host = await StartHostAsync(s =>
            {
                s.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
                s.AddHoistJwtAuth(jwt => jwt.SigningKey = Convert.ToBase64String(new byte[64]));
            });

            host.SetBearer(TestTokens.Create("alice", "alice@test.com", "Alice"));
            Assert.AreEqual(401, (int)(await host.Client.GetAsync("xh/getIdentity")).StatusCode);
        }

        [TestMethod]
        public async Task TestStartupRequiresAuthProvider()
        {
            var ex = await Assert.ThrowsExactlyAsync<HoistException>(() =>
                StartHostAsync(s => s.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null))));
            StringAssert.Contains(ex.Message, "AddHoistJwtAuth");
        }

        //------------------------
        // User directory
        //------------------------
        [TestMethod]
        public async Task TestUserServiceRegisteredBeforeAddHoist()
        {
            await using var host = await StartHostAsync(s =>
            {
                s.AddSingleton<IHoistUserService, DirectoryUsers>();
                s.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
                s.AddHoistJwtAuth(jwt => jwt.SigningKey = TestTokens.SigningKey);
            });
            await AssertDirectoryUsersInUseAsync(host);
        }

        [TestMethod]
        public async Task TestUserServiceRegisteredAfterAddHoist()
        {
            await using var host = await HoistTestHost.StartAsync(configureServices: s => s.AddSingleton<IHoistUserService, DirectoryUsers>());
            await AssertDirectoryUsersInUseAsync(host);
        }

        private static async Task AssertDirectoryUsersInUseAsync(HoistTestHost host)
        {
            Assert.IsInstanceOfType(host.Services.GetRequiredService<IHoistUserService>(), typeof(DirectoryUsers));
            Assert.IsNull(host.Services.GetService<IHoistUserProvisioner>());

            await host.LoginAsync("bob");   // known to the directory
            Assert.AreEqual("bob", await IdentityUsernameAsync(host.Client));

            var stranger = host.NewClient();   // valid token, but not in the directory
            stranger.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create("zed", "zed@test.com", "Zed"));
            Assert.AreEqual(401, (int)(await stranger.GetAsync("xh/getIdentity")).StatusCode);
        }

        //------------------------
        // Impersonation targets (IHoistUserService.ImpersonationTargetsAsync)
        //------------------------
        private static async Task<HoistTestHost> StartWithTargetsAsync()
        {
            var host = await HoistTestHost.StartAsync(configureServices: s => s.AddSingleton<IHoistUserService, DirectoryUsers>());
            var roles = host.Services.GetRequiredService<DefaultRoleService>();
            await roles.AssignRoleAsync("admin", "HOIST_ADMIN");
            await roles.AssignRoleAsync("carol", "HOIST_ADMIN");
            await roles.AssignRoleAsync("bob", "HOIST_IMPERSONATOR");
            await host.Services.GetRequiredService<ConfigService>().SetValueAsync("xhEnableImpersonation", "true", "test");
            await host.LoginAsync("admin");
            return host;
        }

        private static async Task<List<string>> TargetsAsync(HttpClient client)
        {
            var json = await JsonAssert.ReadJsonAsync(await client.GetAsync("xh/impersonationTargets"));
            return json.EnumerateArray().Select(t => t.GetProperty("username").GetString()).OrderBy(n => n).ToList();
        }

        [TestMethod]
        public async Task TestImpersonationTargetsComeFromUserService()
        {
            await using var host = await StartWithTargetsAsync();
            CollectionAssert.AreEqual(new[] { "bob", "carol", "dave" }, await TargetsAsync(host.Client));
        }

        [TestMethod]
        public async Task TestOverriddenTargetsStillHideAdminsFromNonAdmins()
        {
            await using var host = await StartWithTargetsAsync();
            var bob = host.NewClient();
            await host.LoginAsync("bob", bob);
            CollectionAssert.AreEqual(new[] { "bob", "dave" }, await TargetsAsync(bob));
        }

        [TestMethod]
        public async Task TestImpersonateOnlyTargets()
        {
            await using var host = await StartWithTargetsAsync();

            var json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/impersonate", Form(("username", "erin"))), 500);
            Assert.AreEqual("'admin' is not authorized to impersonate 'erin'", json.GetProperty("message").GetString());

            json = await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/impersonate", Form(("username", "nobody"))), 500);
            Assert.AreEqual("User 'nobody' not found", json.GetProperty("message").GetString());

            await JsonAssert.NoContentAsync(await host.Client.PostAsync("xh/impersonate", Form(("username", "dave"))));
            Assert.AreEqual("dave", await IdentityUsernameAsync(host.Client));
        }

        //------------------------
        // Living in the host's pipeline
        //------------------------
        [TestMethod]
        public async Task TestHostEndpointsAreUntouched()
        {
            var builder = HoistTestHost.CreateBuilder();
            builder.Services.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
            builder.Services.AddHoistJwtAuth(jwt => jwt.SigningKey = TestTokens.SigningKey);
            var app = builder.Build();
            app.UseHoist();
            app.MapHoist();
            app.MapGet("/host/hello", () => "hello");
            await using var host = await HoistTestHost.StartAsync(app);

            // No Hoist session or token needed, and no Hoist error rendering, for the host's own routes.
            Assert.AreEqual("hello", await host.Client.GetStringAsync("/host/hello"));
            var missing = await host.Client.GetAsync("/host/missing");
            Assert.AreEqual(404, (int)missing.StatusCode);
            Assert.AreEqual("", await missing.Content.ReadAsStringAsync());

            // Hoist's own routes still require authentication.
            Assert.AreEqual(401, (int)(await host.Client.GetAsync("xh/getConfig")).StatusCode);
        }

        [TestMethod]
        public async Task TestHostGlobalFiltersDoNotApply()
        {
            // A typical API host: JwtBearer, a global AuthorizeFilter and a global Consumes("application/json").
            var builder = HoistTestHost.CreateBuilder();
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            builder.Services.AddAuthorization();
            builder.Services.AddControllers(o =>
            {
                o.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                o.Filters.Add(new ConsumesAttribute("application/json"));
                o.Filters.Add(new ProducesAttribute("application/json"));
            });
            builder.Services.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
            builder.Services.AddHoistJwtAuth(jwt => jwt.SigningKey = TestTokens.SigningKey);
            var app = builder.Build();
            app.UseRouting();
            app.UseHoist();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapHoist();
            await using var host = await HoistTestHost.StartAsync(app);

            // Whitelisted, anonymous, and a form post - none blocked by the host's filters.
            await JsonAssert.ReadJsonAsync(await host.Client.GetAsync("xh/version"));
            await host.LoginAsync("alice");
            Assert.AreEqual("alice", await IdentityUsernameAsync(host.Client));
            await JsonAssert.ReadJsonAsync(await host.Client.PostAsync("xh/setPrefs", Form(("updates", "{}"), ("clientUsername", "alice"))));
        }

        [TestMethod]
        public async Task TestPortRestrictsHoistEndpoints()
        {
            await using var host = await HoistTestHost.StartAsync(o => o.Port = 8080);
            var other = host.NewClient("http://localhost:5080/");
            var hoist = host.NewClient("http://localhost:8080/");

            await JsonAssert.ReadJsonAsync(await hoist.GetAsync("xh/version"));
            Assert.AreEqual(404, (int)(await other.GetAsync("xh/version")).StatusCode);

            // Unknown Hoist routes: a Hoist JSON 404 on the Hoist port only.
            await host.LoginAsync("alice", hoist);
            var json = await JsonAssert.ReadJsonAsync(await hoist.GetAsync("xh/noSuchAction"), 404);
            Assert.AreEqual("NotFoundException", json.GetProperty("name").GetString());
            Assert.AreEqual("", await (await other.GetAsync("xh/noSuchAction")).Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task TestMissingUseHoistRefusesHoistEndpoints()
        {
            var builder = HoistTestHost.CreateBuilder();
            builder.Services.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
            builder.Services.AddHoistJwtAuth(jwt => jwt.SigningKey = TestTokens.SigningKey);
            var app = builder.Build();
            app.MapHoist();
            await using var host = await HoistTestHost.StartAsync(app);

            // The TestServer rethrows unhandled exceptions to the caller; a real server answers 500.
            var ex = await Assert.ThrowsExactlyAsync<HoistException>(() => host.Client.GetAsync("xh/version"));
            StringAssert.Contains(ex.Message, "UseHoist");
        }

        //------------------------
        // Test implementations
        //------------------------
        public class HostAuthConfig
        {
            public string SecretKey { get; set; }
        }

        /// <summary>A classic host shape: Startup, settings bound from configuration, key from a host service.</summary>
        public class ClassicStartup
        {
            public void ConfigureServices(IServiceCollection services)
            {
                services.AddHoist(o => HoistTestHost.ApplyTestOptions(o, null));
                services.AddHoistJwtAuth((jwt, sp) => jwt.SigningKey = sp.GetRequiredService<HostAuthConfig>().SecretKey);
            }

            public void Configure(IApplicationBuilder app)
            {
                app.UseRouting();
                app.UseHoist();
                app.UseEndpoints(endpoints => endpoints.MapHoist());
            }
        }

        /// <summary>Records whether Hoist was initialized when this hosted service started.</summary>
        public class StartupProbe : IHostedService
        {
            private IServiceProvider services;

            public bool HoistInitializedWhenStarted { get; private set; }

            public StartupProbe With(IServiceProvider services)
            {
                this.services = services;
                return this;
            }

            public Task StartAsync(CancellationToken cancellationToken)
            {
                HoistInitializedWhenStarted = services.GetRequiredService<HoistBootstrapper>().IsInitialized;
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        /// <summary>A host's user directory, narrowing who may be impersonated (erin may not).</summary>
        public class DirectoryUsers : IHoistUserService
        {
            private readonly List<HoistUser> users = new[] { "admin", "bob", "carol", "dave", "erin" }
                .Select(n => new HoistUser(n, $"{n}@test.com", n)).ToList();

            public Task<HoistUser> FindAsync(string username) => Task.FromResult(users.FirstOrDefault(u => u.Username == username));

            public Task<List<HoistUser>> ListAsync(bool activeOnly) => Task.FromResult(users.ToList());

            public Task<List<HoistUser>> ImpersonationTargetsAsync(HoistUser authUser) =>
                Task.FromResult(users.Where(u => u.Username is "bob" or "carol" or "dave").ToList());
        }
    }
}
