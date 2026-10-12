using System.Net;
using System.Net.Http.Headers;
using Hoist.Api.Hosting;
using Hoist.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Support
{
    /// <summary>
    /// A host app on an in-memory TestServer, wired the way a real host wires Hoist: AddHoist,
    /// AddHoistJwtAuth, UseHoist, MapHoist (Hoist initializes as the host starts). Each host gets its own SQLite
    /// in-memory database, so tests are isolated without any clean-up. The client keeps cookies,
    /// like a browser, so the Hoist session survives between requests.
    /// </summary>
    public class HoistTestHost : IAsyncDisposable
    {
        private readonly TestServer server;
        private readonly WebApplication app;   // null when wrapping a host the test owns

        private HoistTestHost(TestServer server, WebApplication app)
        {
            this.server = server;
            this.app = app;
            Client = NewClient();
        }

        public HttpClient Client { get; }
        public IServiceProvider Services => server.Services;

        /// <summary>Clients for a started TestServer whose host the test owns (and stops itself).</summary>
        public static HoistTestHost ForClient(TestServer server) => new HoistTestHost(server, null);

        /// <summary>The standard host. configureServices runs after AddHoist and AddHoistJwtAuth.</summary>
        public static async Task<HoistTestHost> StartAsync(
            Action<HoistOptions> configureOptions = null,
            Action<IServiceCollection> configureServices = null)
        {
            var builder = CreateBuilder();
            builder.Services.AddHoist(o => ApplyTestOptions(o, configureOptions));
            builder.Services.AddHoistJwtAuth(jwt => jwt.SigningKey = TestTokens.SigningKey);
            configureServices?.Invoke(builder.Services);

            var app = builder.Build();
            app.UseHoist();
            app.MapHoist();
            return await StartAsync(app);
        }

        /// <summary>For tests that wire the host themselves: a builder on a TestServer.</summary>
        public static WebApplicationBuilder CreateBuilder()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            return builder;
        }

        /// <summary>For tests that wire the host themselves: starts the built app.</summary>
        public static async Task<HoistTestHost> StartAsync(WebApplication app)
        {
            await app.StartAsync();
            return new HoistTestHost(app.GetTestServer(), app);
        }

        public static HoistOptions TestOptions()
        {
            var options = new HoistOptions();
            ApplyTestOptions(options, null);
            return options;
        }

        public static void ApplyTestOptions(HoistOptions options, Action<HoistOptions> configure)
        {
            options.AppCode = "testApp";
            options.AppName = "Test App";
            options.AppVersion = "1.2.3";
            options.AppBuild = "abc123";
            options.Environment = "Development";
            options.InstanceName = "test-instance";
            options.Database = new HoistDatabaseOptions
            {
                Provider = "Sqlite",
                ConnectionString = $"Data Source=hoist-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
                Migrate = "EnsureCreated"
            };
            configure?.Invoke(options);
        }

        /// <summary>An independent "browser" against the same server, with its own cookies.</summary>
        public HttpClient NewClient(string baseAddress = "http://localhost/")
        {
            var handler = new CookieHandler(new CookieContainer()) { InnerHandler = server.CreateHandler() };
            return new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        }

        /// <summary>Authenticates as username the way a JWT-based hoist-react client does: Bearer token, then xh/login.</summary>
        public async Task LoginAsync(string username, HttpClient client = null)
        {
            client ??= Client;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(username, $"{username}@test.com", $"{username} Display"));
            var response = await client.PostAsync("xh/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = "*****" }));
            response.EnsureSuccessStatusCode();
        }

        public void SetBearer(string token)
        {
            Client.DefaultRequestHeaders.Authorization = token == null ? null : new AuthenticationHeaderValue("Bearer", token);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (app == null) return;
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private class CookieHandler : DelegatingHandler
        {
            private readonly CookieContainer cookies;

            public CookieHandler(CookieContainer cookies)
            {
                this.cookies = cookies;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var header = cookies.GetCookieHeader(request.RequestUri);
                if (!string.IsNullOrEmpty(header)) request.Headers.Add("Cookie", header);

                var response = await base.SendAsync(request, cancellationToken);
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var c in setCookies) cookies.SetCookies(request.RequestUri, c);
                }
                return response;
            }
        }
    }
}
