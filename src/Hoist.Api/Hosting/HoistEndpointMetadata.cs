using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Hoist.Api.Hosting
{
    /// <summary>
    /// Marks an endpoint as Hoist's. The Hoist middleware (UseHoist) only acts on marked endpoints, so the
    /// host's own endpoints are untouched.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class HoistEndpointAttribute : Attribute
    {
    }

    /// <summary>
    /// Hoist actions read query and form params themselves, whatever the content type, as Grails does.
    /// As the [Consumes] filter closest to the action, this overrides a host's global [Consumes]
    /// (e.g. a global Consumes("application/json"), which would 415 the form posts of xh/login).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class AcceptAnyContentTypeAttribute : ConsumesAttribute, IResourceFilter, IActionConstraint
    {
        // The base constructor needs a content type. It is cleared again: no content types means any,
        // for the routing metadata MVC builds from ContentTypes. The base checks are replaced below by pass-throughs.
        public AcceptAnyContentTypeAttribute() : base("application/json")
        {
            ContentTypes = new MediaTypeCollection();
        }

        void IResourceFilter.OnResourceExecuting(ResourceExecutingContext context)
        {
        }

        void IResourceFilter.OnResourceExecuted(ResourceExecutedContext context)
        {
        }

        bool IActionConstraint.Accept(ActionConstraintContext context) => true;
    }

    /// <summary>Restricts the Hoist controllers to HoistOptions.Port, when set.</summary>
    public class HoistPortConvention : IControllerModelConvention
    {
        private readonly int port;

        public HoistPortConvention(int port)
        {
            this.port = port;
        }

        public void Apply(ControllerModel controller)
        {
            if (!typeof(Controllers.BaseController).IsAssignableFrom(controller.ControllerType)) return;
            foreach (var selector in controller.Selectors)
            {
                selector.EndpointMetadata.Add(new HostAttribute($"*:{port}"));
            }
        }
    }
}
