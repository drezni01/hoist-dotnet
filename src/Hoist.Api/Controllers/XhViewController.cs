using Hoist.Api.Security;
using Hoist.Core.Services;
using Hoist.Core.Utils;
using Microsoft.AspNetCore.Mvc;

namespace Hoist.Api.Controllers
{
    /// <summary>Port of hoist-core impl/XhViewController - ViewManager endpoints. See docs/api/views.md.</summary>
    [AccessAll]
    public class XhViewController : BaseController
    {
        private readonly ViewService viewService;

        public XhViewController(ViewService viewService)
        {
            this.viewService = viewService;
        }

        //------------------------
        // ViewManager state + support
        //------------------------
        [Route("xhView/allData")]
        public async Task<IActionResult> AllData(string type, string viewInstance)
        {
            return RenderJson(await viewService.GetAllDataAsync(type, viewInstance));
        }

        [Route("xhView/updateState")]
        public async Task<IActionResult> UpdateState(string type, string viewInstance)
        {
            return RenderJson(await viewService.UpdateStateAsync(type, viewInstance, await ParseRequestJsonAsync()));
        }

        //------------------------
        // Individual view management
        //------------------------
        [Route("xhView/get")]
        public async Task<IActionResult> Get(string token)
        {
            return RenderJson(await viewService.GetAsync(token));
        }

        [Route("xhView/create")]
        public async Task<IActionResult> Create()
        {
            return RenderJson(await viewService.CreateAsync(await ParseRequestJsonAsync()));
        }

        [Route("xhView/delete")]
        public async Task<IActionResult> Delete(string tokens)
        {
            await viewService.DeleteAsync(SplitTokens(tokens));
            return RenderSuccess();
        }

        [Route("xhView/updateInfo")]
        public async Task<IActionResult> UpdateInfo(string token)
        {
            return RenderJson(await viewService.UpdateInfoAsync(token, await ParseRequestJsonAsync()));
        }

        [Route("xhView/bulkUpdateInfo")]
        public async Task<IActionResult> BulkUpdateInfo(string tokens)
        {
            await viewService.BulkUpdateInfoAsync(SplitTokens(tokens), await ParseRequestJsonAsync());
            return RenderSuccess();
        }

        [Route("xhView/updateValue")]
        public async Task<IActionResult> UpdateValue(string token)
        {
            var body = await ReadBodyAsync();
            return RenderJson(await viewService.UpdateValueAsync(token, string.IsNullOrWhiteSpace(body) ? null : ParseNode(body)));
        }

        //------------------------
        // Group management
        //------------------------
        [Route("xhView/renameGroup")]
        public async Task<IActionResult> RenameGroup(string type)
        {
            var data = await ParseRequestJsonAsync();
            var isGlobal = data["isGlobal"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var b) && b;
            return RenderJson(await viewService.RenameGroupAsync(type, JsonUtils.ToStoredString(data["from"]), JsonUtils.ToStoredString(data["to"]), isGlobal));
        }

        private static List<string> SplitTokens(string tokens) => (tokens ?? "").Split(',').ToList();
    }
}
