using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hoist.Core
{
    public static class HoistCoreServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the Core services. They are singletons. Hosts replace the
        /// extension points (IHoistUserService, IRoleService) by registering their own, before or after.
        /// HoistOptions must be registered too.
        /// </summary>
        public static IServiceCollection AddHoistCore(this IServiceCollection services)
        {
            services.AddSingleton(TypedConfigRegistry.BuiltIn);
            services.AddSingleton<ConfigService>();
            services.AddSingleton<HoistBootstrapper>();
            services.AddSingleton<IdentityService>();
            services.AddSingleton<EnvironmentService>();
            services.AddSingleton<AlertBannerService>();
            services.AddSingleton<PrefService>();
            services.AddSingleton<TrackService>();
            services.AddSingleton<JsonBlobService>();
            services.AddSingleton<ViewService>();
            services.AddSingleton<TrackLogAdminService>();
            services.AddSingleton<RoleAdminService>();
            services.AddSingleton<IHoistInitializer>(sp => sp.GetRequiredService<PrefService>());

            services.AddSingleton<DefaultRoleService>();
            services.TryAddSingleton<IRoleService>(sp => sp.GetRequiredService<DefaultRoleService>());
            services.AddSingleton<IHoistInitializer>(sp => sp.GetRequiredService<DefaultRoleService>());

            // Defaults only: a host's own IHoistUserService wins whether it is registered before or after AddHoist.
            services.AddSingleton<DefaultUserService>();
            services.TryAddSingleton<IHoistUserService>(sp => sp.GetRequiredService<DefaultUserService>());
            // Follows the registered IHoistUserService - null when it does not provision.
            services.AddSingleton<IHoistUserProvisioner>(sp => sp.GetRequiredService<IHoistUserService>() as IHoistUserProvisioner);
            return services;
        }
    }
}
