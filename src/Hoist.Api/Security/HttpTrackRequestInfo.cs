using Hoist.Core.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Hoist.Api.Security
{
    /// <summary>Request headers for activity tracking, from the current HTTP request.</summary>
    public class HttpTrackRequestInfo : ITrackRequestInfo
    {
        private readonly IHttpContextAccessor accessor;

        public HttpTrackRequestInfo(IHttpContextAccessor accessor)
        {
            this.accessor = accessor;
        }

        public bool HasRequest => accessor.HttpContext != null;
        public string UserAgent => Header("User-Agent");
        public string SecChUa => Header("Sec-Ch-UA");
        public string SecChUaPlatform => Header("Sec-Ch-UA-Platform");

        private string Header(string name)
        {
            var value = accessor.HttpContext?.Request.Headers[name].ToString();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
