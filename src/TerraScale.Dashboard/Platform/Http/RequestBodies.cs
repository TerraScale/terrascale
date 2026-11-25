using Microsoft.AspNetCore.Http.Features;

namespace TerraScale.Dashboard.Platform.Http
{
    public static class RequestBodies
    {
        public const long SourceForm = 3 * 256 * 1024 + 16 * 1024;

        public static void Allow(HttpContext http, long bytes)
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = bytes;
            }
        }
    }
}
