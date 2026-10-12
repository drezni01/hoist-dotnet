using Hoist.Api.Middleware;
using Hoist.Api.Security;
using Hoist.Core.Configuration;
using Hoist.Core.Exceptions;
using Hoist.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Api.Hosting
{
    /// <summary>Wires Hoist into the host's pipeline: UseHoist and MapHoist (InitializeHoistAsync runs on start).</summary>
    public static class HoistApplicationBuilderExtensions
    {
        /// <summary>
        /// Resolves the options and the auth provider (so binding and key errors surface here), then
        /// creates/validates the schema and seeds the built-in configs, prefs and roles. Runs
        /// automatically as the host starts; calling it explicitly as well is harmless - it runs once.
        /// </summary>
        public static async Task InitializeHoistAsync(this IServiceProvider services)
        {
            services.GetRequiredService<HoistOptions>();
            if (services.GetService<IHoistAuthenticationService>() == null)
            {
                throw new HoistException("No Hoist auth provider is registered - call services.AddHoistJwtAuth(...).");
            }
            await services.GetRequiredService<HoistBootstrapper>().InitAsync();
        }

        /// <summary>
        /// Adds the Hoist middleware (error rendering, session and authentication). Place it after
        /// routing - explicit UseRouting(), or the implicit one of a WebApplication - and before the
        /// endpoints. It acts only on Hoist endpoints.
        /// </summary>
        public static IApplicationBuilder UseHoist(this IApplicationBuilder app)
        {
            app.UseMiddleware<HoistExceptionMiddleware>();
            app.UseMiddleware<HoistAuthMiddleware>();
            return app;
        }

        /// <summary>
        /// Maps the Hoist controllers and a JSON 404 for unknown Hoist routes (xh/*, xhView/*, rest/*, *Admin/*).
        /// Safe alongside the host's own MapControllers().
        /// </summary>
        public static IEndpointRouteBuilder MapHoist(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapControllers();

            var notFound = endpoints.MapFallback(@"{prefix:regex(^(xh|xhView|rest|\w+Admin)$)}/{**rest}", _ => throw new NotFoundException())
                .WithMetadata(new HoistEndpointAttribute());
            var port = endpoints.ServiceProvider.GetRequiredService<HoistOptions>().Port;
            if (port != null) notFound.RequireHost($"*:{port}");
            return endpoints;
        }
    }
}
