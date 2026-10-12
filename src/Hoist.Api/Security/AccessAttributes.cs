using System.Reflection;
using Hoist.Core.Exceptions;
using Hoist.Core.Services;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Hoist.Api.Security
{
    /// <summary>Base of the hoist-core io.xh.hoist.security access annotations.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public abstract class HoistAccessAttribute : Attribute
    {
    }

    /// <summary>@AccessAll - any authenticated user.</summary>
    public class AccessAllAttribute : HoistAccessAttribute
    {
    }

    /// <summary>@AccessRequiresRole</summary>
    public class AccessRequiresRoleAttribute : HoistAccessAttribute
    {
        public AccessRequiresRoleAttribute(string role)
        {
            Role = role;
        }

        public string Role { get; }
    }

    /// <summary>@AccessRequiresAnyRole</summary>
    public class AccessRequiresAnyRoleAttribute : HoistAccessAttribute
    {
        public AccessRequiresAnyRoleAttribute(params string[] roles)
        {
            Roles = roles;
        }

        public string[] Roles { get; }
    }

    /// <summary>@AccessRequiresAllRoles</summary>
    public class AccessRequiresAllRolesAttribute : HoistAccessAttribute
    {
        public AccessRequiresAllRolesAttribute(params string[] roles)
        {
            Roles = roles;
        }

        public string[] Roles { get; }
    }

    /// <summary>
    /// Port of hoist-core HoistInterceptor: the access annotation on the action wins over the one on
    /// the controller; no annotation means no access. Checked against the APPARENT user.
    /// </summary>
    public class HoistAccessFilter : IAsyncAuthorizationFilter
    {
        private readonly IdentityService identityService;

        public HoistAccessFilter(IdentityService identityService)
        {
            this.identityService = identityService;
        }

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            if (context.ActionDescriptor is not ControllerActionDescriptor action) return;

            // Without the Hoist middleware no one is authenticated - refuse rather than serve unprotected.
            if (!context.HttpContext.Items.ContainsKey(Middleware.HoistAuthMiddleware.RanKey))
            {
                throw new HoistException("Hoist endpoints were reached without the Hoist middleware - call app.UseHoist() after routing.");
            }

            var access = action.MethodInfo.GetCustomAttribute<HoistAccessAttribute>(inherit: true)
                ?? action.ControllerTypeInfo.GetCustomAttribute<HoistAccessAttribute>(inherit: true);

            var user = await identityService.GetUserAsync();
            var allowed = access switch
            {
                AccessAllAttribute => true,
                AccessRequiresRoleAttribute r => await identityService.HasRoleAsync(user, r.Role),
                AccessRequiresAnyRoleAttribute r => await identityService.HasAnyRoleAsync(user, r.Roles),
                AccessRequiresAllRolesAttribute r => await identityService.HasAllRolesAsync(user, r.Roles),
                _ => false
            };

            if (!allowed)
            {
                throw new NotAuthorizedException($"You do not have the required role(s) for this action. Currently logged in as: {identityService.Username}.");
            }
        }
    }
}
