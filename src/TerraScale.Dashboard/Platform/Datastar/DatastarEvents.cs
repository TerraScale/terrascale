using System.Globalization;
using System.Text.Json;
using RazorSlices;

namespace TerraScale.Dashboard.Platform.Datastar
{
    public static class DatastarEvents
    {
        public static async Task PatchElementsAsync(HttpContext context, RazorSlice slice)
        {
            slice.HttpContext = context;
            slice.ServiceProvider = context.RequestServices;
            var html = await slice.RenderAsync(cancellationToken: context.RequestAborted);
            await PatchElementsAsync(context, html, context.RequestAborted);
        }

        public static async Task PatchElementsAsync(HttpContext context, string html, CancellationToken cancellationToken)
        {
            if (context.Response.ContentType == "text/event-stream")
            {
                await context.Response.WriteAsync("event: datastar-patch-elements\n", cancellationToken);
                foreach (var line in html.ReplaceLineEndings("\n").Split('\n'))
                {
                    await context.Response.WriteAsync("data: elements " + line + "\n", cancellationToken);
                }

                await context.Response.WriteAsync("\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(html, cancellationToken);
        }

        public static Task LiveAuthAsync(HttpContext context, int status, CancellationToken cancellationToken) =>
            WriteLiveEventAsync(context, "ts-live-auth", status.ToString(CultureInfo.InvariantCulture), cancellationToken);

        public static Task LiveCompleteAsync(HttpContext context, CancellationToken cancellationToken) =>
            WriteLiveEventAsync(context, "ts-live-complete", "complete", cancellationToken);

        private static async Task WriteLiveEventAsync(HttpContext context, string name, string value, CancellationToken cancellationToken)
        {
            await context.Response.WriteAsync($"event: {name}\ndata: {value}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        public static async Task KeepAliveAsync(HttpContext context, CancellationToken cancellationToken)
        {
            await context.Response.WriteAsync(": keepalive\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        public static async Task PatchSignalsAsync(HttpContext context, object signals)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Accel-Buffering"] = "no";
            }
            await context.Response.WriteAsync($"event: datastar-patch-signals\ndata: signals {JsonSerializer.Serialize(signals)}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
}
