using Hoist.Core.Interfaces;
using Hoist.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Hoist.Tests.Support
{
    /// <summary>A host-supplied user directory, as a host app would provide.</summary>
    public class FakeUserService : IHoistUserService
    {
        private readonly Dictionary<string, HoistUser> users = new Dictionary<string, HoistUser>();

        public FakeUserService Add(string username, bool active = true, string email = null, string displayName = null)
        {
            users[username] = new HoistUser(username, email ?? $"{username}@test.com", displayName ?? username, active);
            return this;
        }

        public Task<HoistUser> FindAsync(string username) =>
            Task.FromResult(username != null && users.TryGetValue(username, out var u) ? u : null);

        public Task<List<HoistUser>> ListAsync(bool activeOnly) =>
            Task.FromResult(users.Values.Where(u => !activeOnly || u.Active).ToList());
    }

    public static class FakeUserServiceExtensions
    {
        public static IServiceCollection AddSingletonUserService(this IServiceCollection services, IHoistUserService userService)
        {
            return services.AddSingleton(userService);
        }
    }
}
