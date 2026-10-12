using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Hoist.Core.Services
{
    /// <summary>Creates or verifies the xh_* schema (Infra implements it).</summary>
    public interface IHoistSchema
    {
        /// <summary>Creates the schema if empty (per Migrate), then validates it (per ValidateSchema).</summary>
        Task EnsureSchemaAsync();

        /// <summary>Mapped tables and columns missing from the database, e.g. "missing column xh_config.note".</summary>
        Task<IReadOnlyList<string>> FindProblemsAsync();
    }

    /// <summary>
    /// Port of hoist-core BootStrap.init, minus clustering, monitoring and log setup: ensure the
    /// schema, then the built-in configs, prefs and roles. Runs once before the server takes requests.
    /// </summary>
    public class HoistBootstrapper
    {
        private readonly IHoistSchema schema;
        private readonly ConfigService configService;
        private readonly IEnumerable<IHoistInitializer> initializers;
        private readonly HoistOptions options;
        private readonly ILogger<HoistBootstrapper> logger;

        public HoistBootstrapper(
            IHoistSchema schema,
            ConfigService configService,
            IEnumerable<IHoistInitializer> initializers,
            HoistOptions options,
            ILogger<HoistBootstrapper> logger)
        {
            this.schema = schema;
            this.configService = configService;
            this.initializers = initializers;
            this.options = options;
            this.logger = logger;
        }

        /// <summary>True once InitAsync has completed - requests are refused until then.</summary>
        public bool IsInitialized { get; private set; }

        private readonly object initLock = new object();
        private Task initTask;

        /// <summary>Runs the bootstrap once; later or concurrent calls await the same run.</summary>
        public Task InitAsync()
        {
            lock (initLock)
            {
                return initTask ??= RunInitAsync();
            }
        }

        private async Task RunInitAsync()
        {
            logger.LogInformation("Initializing Hoist {AppName} v{AppVersion} - {Environment} (hoist-core API {HoistCoreVersion})",
                options.AppName, options.AppVersion, options.Environment, HoistOptions.HoistCoreVersion);

            await schema.EnsureSchemaAsync();
            await configService.EnsureRequiredConfigsCreatedAsync(BuiltInSpecs.Configs);
            foreach (var initializer in initializers.OrderBy(i => i.Order))
            {
                await initializer.InitAsync();
            }
            IsInitialized = true;
        }
    }

    /// <summary>A service needing one-time setup at startup (e.g. ensuring its prefs or roles exist).</summary>
    public interface IHoistInitializer
    {
        int Order { get; }
        Task InitAsync();
    }
}
