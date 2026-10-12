namespace Hoist.Core.Configuration
{
    /// <summary>
    /// Hoist settings, set by the host in AddHoist(o => ...) - typically by binding a configuration
    /// section. These replace the Grails gradle.properties (xhAppCode, xhAppVersion...) and instance config.
    /// Authentication settings belong to the auth provider (e.g. AddHoistJwtAuth).
    /// </summary>
    public class HoistOptions
    {
        /// <summary>The hoist-core version whose HTTP contract this library implements (see docs/PORT-MANIFEST.yaml).</summary>
        public const string HoistCoreVersion = "42.1.0";

        public const string SectionName = "Hoist";

        public string AppCode { get; set; } = "hoist";
        public string AppName { get; set; } = "Hoist";

        /// <summary>Must exactly match the client build's xhAppVersion or hoist-react refuses to start.</summary>
        public string AppVersion { get; set; } = "0.0.1";
        public string AppBuild { get; set; } = "UNKNOWN";

        /// <summary>One of Production, Beta, Staging, Development, Test, UAT, BCP.</summary>
        public string Environment { get; set; } = "Development";

        public string InstanceName { get; set; } = System.Environment.MachineName;

        /// <summary>
        /// Optional: serve the Hoist endpoints only on this port (e.g. 8080, where hoist-react clients
        /// expect them). The host must listen on it too, e.g. "Urls": "http://localhost:5000;http://localhost:8080".
        /// When unset, the Hoist endpoints answer on every port the host listens on.
        /// </summary>
        public int? Port { get; set; }

        /// <summary>Granted HOIST_ADMIN, HOIST_ADMIN_READER and HOIST_ROLE_MANAGER - only in the Development environment.</summary>
        public string BootstrapAdminUser { get; set; }

        public HoistDatabaseOptions Database { get; set; } = new HoistDatabaseOptions();

        /// <summary>
        /// Instance config overrides for AppConfig values, keyed by config name - the equivalent of
        /// /etc/hoist/conf/{appCode}.yml. Environment variables APP_{APPCODE}_{KEY} take precedence.
        /// </summary>
        public Dictionary<string, string> InstanceConfig { get; set; } = new Dictionary<string, string>();

        public bool IsProduction => Environment == "Production";
        public bool IsDevelopment => Environment == "Development";
    }

    public class HoistDatabaseOptions
    {
        /// <summary>MySql (production) or Sqlite (tests / demo).</summary>
        public string Provider { get; set; } = "MySql";

        public string ConnectionString { get; set; }

        /// <summary>MySQL server version used by Pomelo, e.g. "8.0.36".</summary>
        public string ServerVersion { get; set; } = "8.0.36";

        /// <summary>
        /// EnsureCreated: create the xh_* tables if the (dedicated) Hoist schema is empty - a no-op on an
        /// existing hoist-core schema. None: never touch the schema.
        /// </summary>
        public string Migrate { get; set; } = "EnsureCreated";

        /// <summary>
        /// Check at startup that every mapped xh_* table and column exists. A mismatch is logged and fails
        /// startup. Extra columns in the database are allowed.
        /// </summary>
        public bool ValidateSchema { get; set; } = true;

        /// <summary>
        /// Log the SQL of Hoist's own queries (EF Database.Command). Off by default: EF uses one log
        /// category for every DbContext, so a host that logs its own SQL would otherwise log Hoist's too.
        /// Failed commands are logged either way.
        /// </summary>
        public bool LogSql { get; set; }

        /// <summary>
        /// Time zone the DATETIME columns are stored in. Hibernate wrote java.util.Date values in the JVM's
        /// local zone, so the default is the local zone of this machine.
        /// </summary>
        public string TimeZone { get; set; }
    }
}
