using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Utils;

namespace Hoist.Infra.Configuration
{
    /// <summary>
    /// Port of InstanceConfigUtils.getInstanceConfig: an environment variable APP_{APPCODE}_{KEY}
    /// (upper snake case) wins over the Hoist:InstanceConfig section, which stands in for the
    /// /etc/hoist/conf/{appCode}.yml file.
    /// </summary>
    public class InstanceConfig : IInstanceConfig
    {
        private readonly HoistOptions options;
        private readonly Func<string, string> getEnv;

        public InstanceConfig(HoistOptions options) : this(options, Environment.GetEnvironmentVariable) { }

        public InstanceConfig(HoistOptions options, Func<string, string> getEnv)
        {
            this.options = options;
            this.getEnv = getEnv;
        }

        public string Get(string key)
        {
            var envValue = getEnv(EnvVarName(key));
            if (!string.IsNullOrEmpty(envValue)) return envValue;
            return options.InstanceConfig != null && options.InstanceConfig.TryGetValue(key, out var v) ? v : null;
        }

        public string EnvVarName(string key) => $"APP_{GroovyStrings.ToUpperSnakeCase(options.AppCode)}_{GroovyStrings.ToUpperSnakeCase(key)}";
    }
}
