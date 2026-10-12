using Hoist.Core;
using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Services;
using Hoist.Infra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hoist.Tests.Support
{
    /// <summary>
    /// Real Core services over a private in-memory SQLite database: services
    /// are resolved from DI, not mocked. Only identity is faked.
    /// </summary>
    public sealed class TestServices : IDisposable
    {
        private TestServices(ServiceProvider provider, FakeIdentity identity, HoistOptions options)
        {
            Provider = provider;
            Identity = identity;
            Options = options;
        }

        public ServiceProvider Provider { get; }
        public FakeIdentity Identity { get; }
        public HoistOptions Options { get; }

        public T Get<T>() => Provider.GetRequiredService<T>();

        public static async Task<TestServices> CreateAsync(Action<HoistOptions> configure = null, Action<IServiceCollection> services = null)
        {
            var options = HoistTestHost.TestOptions();
            configure?.Invoke(options);

            var identity = new FakeIdentity();
            var sc = new ServiceCollection();
            sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            sc.AddSingleton(options);
            sc.AddHoistCore();
            sc.AddHoistInfra();
            sc.AddSingleton<IIdentityAccessor>(identity);
            sc.AddSingleton<ITrackRequestInfo>(new NoRequestInfo());
            services?.Invoke(sc);

            var provider = sc.BuildServiceProvider();
            await provider.GetRequiredService<HoistBootstrapper>().InitAsync();
            return new TestServices(provider, identity, options);
        }

        public void Dispose() => Provider.Dispose();
    }

    public class NoRequestInfo : ITrackRequestInfo
    {
        public bool HasRequest => false;
        public string UserAgent => null;
        public string SecChUa => null;
        public string SecChUaPlatform => null;
    }

    public class FakeIdentity : IIdentityAccessor
    {
        public string Username { get; set; }
        public string AuthUsername { get; set; }

        public FakeIdentity As(string username, string authUsername = null)
        {
            Username = username;
            AuthUsername = authUsername ?? username;
            return this;
        }
    }
}
