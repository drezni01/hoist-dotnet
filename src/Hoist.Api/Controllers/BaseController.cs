using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hoist.Api.Hosting;
using Hoist.Api.Json;
using Hoist.Api.Security;
using Hoist.Core.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hoist.Api.Controllers
{
    /// <summary>
    /// Port of hoist-core BaseController. Deliberately not an [ApiController]: simple action
    /// parameters bind from query string AND form body, as Grails 'params' does.
    /// Hoist does its own authentication and access checks (HoistAuthMiddleware, HoistAccessFilter), so
    /// its controllers opt out of the host's authorization ([AllowAnonymous]) and global [Consumes], and
    /// stay out of the host's API docs: their Grails-style actions accept any HTTP method, which Swagger rejects.
    /// </summary>
    [HoistEndpoint]
    [AllowAnonymous]
    [AcceptAnyContentType]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ServiceFilter(typeof(HoistAccessFilter))]
    public abstract class BaseController : Controller
    {
        public const string JsonContentType = "application/json; charset=UTF-8";

        protected ContentResult RenderJson(object value)
        {
            return new ContentResult
            {
                Content = value == null ? "null" : HoistJson.Serialize(value),
                ContentType = JsonContentType,
                StatusCode = 200
            };
        }

        /// <summary>renderSuccess(): 204 with an empty body.</summary>
        protected IActionResult RenderSuccess()
        {
            Response.ContentType = JsonContentType;
            return StatusCode(204);
        }

        protected async Task<string> ReadBodyAsync()
        {
            Request.EnableBuffering();
            Request.Body.Position = 0;
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            return body;
        }

        /// <summary>parseRequestJSON(): the body as a JSON object. With safeEncode, &amp; &lt; &gt; are HTML-escaped first.</summary>
        protected async Task<JsonObject> ParseRequestJsonAsync(bool safeEncode = false)
        {
            var body = await ReadBodyAsync();
            if (string.IsNullOrWhiteSpace(body)) return new JsonObject();
            if (safeEncode) body = SafeEncode(body);
            var node = ParseNode(body);
            return node as JsonObject ?? throw new RoutineRuntimeException("Expected a JSON object in request body.");
        }

        /// <summary>parseRequestJSONArray(): the body as a JSON array.</summary>
        protected async Task<JsonArray> ParseRequestJsonArrayAsync()
        {
            var body = await ReadBodyAsync();
            if (string.IsNullOrWhiteSpace(body)) return new JsonArray();
            return ParseNode(body) as JsonArray ?? throw new RoutineRuntimeException("Expected a JSON array in request body.");
        }

        /// <summary>parseObject(param): a request param holding a JSON object string.</summary>
        protected static JsonObject ParseObjectParam(string json)
        {
            if (string.IsNullOrEmpty(json)) return new JsonObject();
            return ParseNode(json) as JsonObject ?? throw new RoutineRuntimeException("Expected a JSON object.");
        }

        protected static JsonNode ParseNode(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException e)
            {
                throw new RoutineRuntimeException("Unable to parse request JSON: " + e.Message);
            }
        }

        /// <summary>A request parameter from the query string or form body, as Grails 'params' merges them.</summary>
        protected string Param(string name)
        {
            if (Request.Query.TryGetValue(name, out var q)) return q.ToString();
            if (Request.HasFormContentType && Request.Form.TryGetValue(name, out var f)) return f.ToString();
            return null;
        }

        /// <summary>
        /// ensureClientUsernameMatchesSession: guards user-specific endpoints against a client whose
        /// idea of the current user is stale (e.g. calls made on page unload while starting impersonation).
        /// </summary>
        protected void EnsureClientUsernameMatchesSession(string sessionUsername)
        {
            var clientUsername = Param("clientUsername");
            if (string.IsNullOrEmpty(clientUsername))
            {
                throw new SessionMismatchException("Unable to confirm match between client and session user.");
            }
            if (clientUsername != sessionUsername)
            {
                throw new SessionMismatchException("The reported clientUsername does not match current session user.");
            }
        }

        /// <summary>OWASP Encode.forHtmlContent - escapes only &amp;, &lt; and &gt;.</summary>
        public static string SafeEncode(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
