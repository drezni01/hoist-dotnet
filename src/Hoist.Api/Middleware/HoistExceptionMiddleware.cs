using Hoist.Api.Hosting;
using Hoist.Api.Json;
using Hoist.Core.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Hoist.Api.Middleware
{
    /// <summary>
    /// Port of hoist-core ExceptionHandler + ThrowableSerializer. Renders
    /// {name, message, cause, isRoutine} with falsy values dropped; routine exceptions log at debug.
    /// </summary>
    public class HoistExceptionMiddleware
    {
        private readonly RequestDelegate next;
        private readonly ILogger<HoistExceptionMiddleware> logger;

        public HoistExceptionMiddleware(RequestDelegate next, ILogger<HoistExceptionMiddleware> logger)
        {
            this.next = next;
            this.logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!IsHoistEndpoint(context))
            {
                await next(context);
                return;
            }
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(context, ex, logger);
            }
        }

        public static async Task HandleExceptionAsync(HttpContext context, Exception ex, ILogger logger)
        {
            ex = Unwrap(ex);
            if (IsRoutine(ex)) logger.LogDebug(ex, "{Message}", ex.Message);
            else logger.LogError(ex, "{Message}", ex.Message);

            if (context.Response.HasStarted) return;

            context.Response.Clear();
            context.Response.StatusCode = GetHttpStatus(ex);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(HoistJson.Serialize(FormatException(ex)));
        }

        /// <summary>True for the Hoist controllers and Hoist fallback - the host's own endpoints are left alone.</summary>
        public static bool IsHoistEndpoint(HttpContext context) =>
            context.GetEndpoint()?.Metadata.GetMetadata<HoistEndpointAttribute>() != null;

        public static int GetHttpStatus(Exception ex)
        {
            if (ex is HoistException hex) return hex.StatusCode;
            return 500;
        }

        public static bool IsRoutine(Exception ex) => ex is HoistException hex && hex.IsRoutine;

        public static Dictionary<string, object> FormatException(Exception ex)
        {
            var ret = new Dictionary<string, object>
            {
                ["name"] = ex.GetType().Name,
                ["message"] = ex.Message,
                ["cause"] = ex.InnerException?.Message,
                ["isRoutine"] = IsRoutine(ex)
            };
            return ret.Where(kv => kv.Value != null && !(kv.Value is false) && !(kv.Value is string s && s.Length == 0))
                      .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        private static Exception Unwrap(Exception ex)
        {
            while ((ex is AggregateException || ex is System.Reflection.TargetInvocationException) && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            return ex;
        }
    }
}
