using Hoist.Api.Controllers;
using Hoist.Api.Security;
using Hoist.Core;
using Hoist.Core.Configuration;
using Hoist.Core.Interfaces;
using Hoist.Infra;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace Hoist.Api.Hosting
{
    /// <summary>
    /// Registers Hoist in the host app's own container and MVC. Then, in the pipeline:
    /// app.UseHoist() after routing, and app.MapHoist() with the endpoints. Hoist initializes itself
    /// as the host starts. See docs/decisions/0004-hoist-in-host-pipeline.md.
    ///
    /// The configure lambdas run when the host starts, not when they are registered, so they see the
    /// complete configuration (user secrets included) and, in the overloads taking an IServiceProvider,
    /// the host's own services.
    /// </summary>
    public static class HoistServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the Hoist services and controllers. configure sets the options, e.g.
        /// o => configuration.GetSection("Hoist").Bind(o). Pick an auth provider too (AddHoistJwtAuth).
        /// To use your own user directory, register an IHoistUserService - before or after this call.
        /// </summary>
        public static IServiceCollection AddHoist(this IServiceCollection services, Action<HoistOptions> configure) =>
            services.AddHoist((options, _) => configure(options));

        /// <summary>As AddHoist(configure), with the host's services available to configure.</summary>
        public static IServiceCollection AddHoist(this IServiceCollection services, Action<HoistOptions, IServiceProvider> configure)
        {
            services.AddSingleton(sp =>
            {
                var options = new HoistOptions();
                configure(options, sp);
                return options;
            });

            services.AddHoistCore();
            services.AddHoistInfra();
            services.AddHttpContextAccessor();
            services.AddSingleton<HttpIdentityAccessor>();
            services.AddSingleton<IIdentityAccessor>(sp => sp.GetRequiredService<HttpIdentityAccessor>());
            services.AddSingleton<ITrackRequestInfo, HttpTrackRequestInfo>();
            services.AddDataProtection();
            services.AddSingleton<HoistSession>();
            services.AddSingleton<HoistAccessFilter>();
            services.AddHostedService<HoistInitService>();

            services.AddControllers().ConfigureApplicationPartManager(pm =>
            {
                var assembly = typeof(BaseController).Assembly;
                if (!pm.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == assembly))
                {
                    pm.ApplicationParts.Add(new AssemblyPart(assembly));
                }
            });
            services.AddOptions<MvcOptions>().Configure<HoistOptions>((mvc, options) =>
            {
                if (options.Port != null) mvc.Conventions.Add(new HoistPortConvention(options.Port.Value));
            });
            return services;
        }

        /// <summary>
        /// Auth provider "Jwt": Bearer tokens signed with a shared HS256 key (e.g. tokens the host app
        /// issues), e.g. jwt => configuration.GetSection("Hoist:Auth").Bind(jwt).
        /// </summary>
        public static IServiceCollection AddHoistJwtAuth(this IServiceCollection services, Action<HoistJwtOptions> configure) =>
            services.AddHoistJwtAuth((options, _) => configure(options));

        /// <summary>
        /// As AddHoistJwtAuth(configure), with the host's services available to configure - e.g. to take
        /// the key from the host's own settings: (jwt, sp) => jwt.SigningKey = sp.GetRequiredService&lt;MyAuthSettings&gt;().SigningKey.
        /// A missing or malformed key fails at startup.
        /// </summary>
        public static IServiceCollection AddHoistJwtAuth(this IServiceCollection services, Action<HoistJwtOptions, IServiceProvider> configure)
        {
            services.AddSingleton(sp =>
            {
                var options = new HoistJwtOptions();
                configure(options, sp);
                return options;
            });
            services.AddSingleton<IHoistAuthenticationService, JwtBearerAuthenticationService>();
            return services;
        }
    }
}
