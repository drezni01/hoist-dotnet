using System.Text.Json.Nodes;
using Hoist.Api.Security;
using Hoist.Core.Exceptions;
using Hoist.Core.Models;
using Hoist.Core.Services;
using Hoist.Core.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hoist.Api.Controllers.Admin
{
    /// <summary>
    /// Port of hoist-core RestController + admin/AdminRestController. Routes follow UrlMappings:
    /// rest/{controller}[/{id}] by HTTP method, plus bulkUpdate, bulkDelete and lookupData. Reads need
    /// HOIST_ADMIN_READER; writes need HOIST_ADMIN. Each change is tracked as an 'Audit' entry.
    /// Subclasses set the class-level [Route("rest/xxxAdmin")].
    /// </summary>
    [AccessRequiresRole(HoistRoles.HOIST_ADMIN_READER)]
    public abstract class AdminRestController<T> : BaseController where T : class
    {
        /// <summary>The hoist-core domain class name, used in audit messages ("Created AppConfig").</summary>
        protected abstract string EntityName { get; }
        protected abstract long IdOf(T obj);
        protected abstract Task<List<T>> DoListAsync(JsonObject query);
        protected abstract Task<T> GetAsync(long id);
        protected abstract Task<T> DoCreateAsync(JsonObject data);
        protected abstract Task<T> DoUpdateAsync(T obj, JsonObject data);
        protected abstract Task DoDeleteAsync(T obj);
        protected abstract object Format(T obj);
        protected abstract Task<object> LookupDataAsync();

        /// <summary>preprocessSubmit - by default records the authenticated user as lastUpdatedBy.</summary>
        protected virtual void PreprocessSubmit(JsonObject data)
        {
            data["lastUpdatedBy"] = Identity.AuthUsername;
        }

        protected IdentityService Identity => HttpContext.RequestServices.GetRequiredService<IdentityService>();
        private TrackService TrackService => HttpContext.RequestServices.GetRequiredService<TrackService>();
        private ILogger Logger => HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());

        [HttpGet("")]
        [HttpGet("{id:long}")]
        public async Task<IActionResult> Read(long? id)
        {
            if (id.HasValue)
            {
                var obj = await GetAsync(id.Value);
                return RenderJson(new { data = new[] { obj == null ? null : Format(obj) } });
            }
            var queryParam = Param("query");
            var query = string.IsNullOrEmpty(queryParam) ? new JsonObject() : ParseObjectParam(queryParam);
            var list = await DoListAsync(query);
            return RenderJson(new { data = list.Select(Format).ToList() });
        }

        [HttpPost("")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN)]
        public async Task<IActionResult> Create()
        {
            var data = (await ParseRequestJsonAsync())["data"] as JsonObject ?? new JsonObject();
            PreprocessSubmit(data);
            var obj = await DoCreateAsync(data);
            await NoteChangeAsync(obj, "Created");
            return RenderJson(new { data = Format(obj) });
        }

        [HttpPut("")]
        [HttpPut("{id:long}")]
        [HttpPatch("")]
        [HttpPatch("{id:long}")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN)]
        public async Task<IActionResult> Update()
        {
            var data = (await ParseRequestJsonAsync())["data"] as JsonObject ?? new JsonObject();
            PreprocessSubmit(data);
            var id = ToLong(data["id"]) ?? throw new RoutineRuntimeException("No id provided for update.");
            var obj = await GetAsync(id) ?? throw new NotFoundException($"{EntityName} not found with id {id}");
            obj = await DoUpdateAsync(obj, data);
            await NoteChangeAsync(obj, "Updated");
            return RenderJson(new { data = Format(obj) });
        }

        [Route("bulkUpdate")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN)]
        public async Task<IActionResult> BulkUpdate()
        {
            var body = await ParseRequestJsonAsync();
            var newParams = body["newParams"] as JsonObject ?? new JsonObject();
            int success = 0, fail = 0;
            foreach (var idNode in body["ids"] as JsonArray ?? new JsonArray())
            {
                try
                {
                    var id = ToLong(idNode) ?? throw new RoutineRuntimeException("Invalid id");
                    var obj = await GetAsync(id) ?? throw new NotFoundException($"{EntityName} not found with id {id}");
                    obj = await DoUpdateAsync(obj, (JsonObject)newParams.DeepClone());
                    await NoteChangeAsync(obj, "Updated");
                    success++;
                }
                catch (Exception e)
                {
                    fail++;
                    Logger.LogError(e, "Exception updating {Id}", idNode);
                }
            }
            return RenderJson(new { success, fail });
        }

        [HttpDelete("{id:long}")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN)]
        public async Task<IActionResult> Delete(long id)
        {
            var obj = await GetAsync(id) ?? throw new NotFoundException($"{EntityName} not found with id {id}");
            await DoDeleteAsync(obj);
            await NoteChangeAsync(obj, "Deleted");
            return RenderSuccess();
        }

        [Route("bulkDelete")]
        [AccessRequiresRole(HoistRoles.HOIST_ADMIN)]
        public async Task<IActionResult> BulkDelete()
        {
            var ids = Request.Query["ids"].ToList();
            if (Request.HasFormContentType) ids.AddRange(Request.Form["ids"].ToList());
            int success = 0, fail = 0;
            foreach (var raw in ids)
            {
                try
                {
                    var obj = await GetAsync(long.Parse(raw)) ?? throw new NotFoundException();
                    await DoDeleteAsync(obj);
                    await NoteChangeAsync(obj, "Deleted");
                    success++;
                }
                catch (Exception)
                {
                    fail++;
                }
            }
            return RenderJson(new { success, fail });
        }

        [HttpGet("lookupData")]
        public async Task<IActionResult> LookupData()
        {
            return RenderJson(await LookupDataAsync());
        }

        //------------------------
        // Binding helpers (Grails bindData: only the keys present are bound)
        //------------------------
        protected static void Bind(JsonObject data, string key, Action<string> set)
        {
            if (data.TryGetPropertyValue(key, out var node)) set(node == null ? null : JsonUtils.ToStoredString(node));
        }

        protected static void BindBool(JsonObject data, string key, Action<bool> set)
        {
            if (!data.TryGetPropertyValue(key, out var node) || node == null) return;
            set(node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : GroovyStrings.ToBoolean(node.ToString()));
        }

        protected static long? ToLong(JsonNode node)
        {
            if (node is not JsonValue v) return null;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            return v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : null;
        }

        private Task NoteChangeAsync(T obj, string verb)
        {
            return TrackService.TrackAsync(new TrackEntry
            {
                Msg = $"{verb} {EntityName}",
                Category = "Audit",
                Data = new JsonObject { ["id"] = IdOf(obj) }
            });
        }
    }
}
