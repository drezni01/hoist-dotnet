using Microsoft.Extensions.Hosting;

namespace Hoist.Api.Hosting
{
    /// <summary>
    /// Initializes Hoist as the host starts (InitializeHoistAsync). It runs in StartingAsync, which the
    /// host completes for every lifecycle service before any hosted service starts - including the web
    /// server - so no request can arrive before Hoist is ready, whatever the registration order.
    /// A failure stops the host from starting.
    /// </summary>
    public class HoistInitService : IHostedLifecycleService
    {
        private readonly IServiceProvider services;

        public HoistInitService(IServiceProvider services)
        {
            this.services = services;
        }

        public Task StartingAsync(CancellationToken cancellationToken) => services.InitializeHoistAsync();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
