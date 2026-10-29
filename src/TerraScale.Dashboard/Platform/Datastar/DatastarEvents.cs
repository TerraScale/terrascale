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
            await SendAsync(context, "datastar-patch-elements", "elements", html);
        }

        public static Task PatchSignalsAsync(HttpContext context, object signals)
        {
            return SendAsync(context, "datastar-patch-signals", "signals", JsonSerializer.Serialize(signals));
        }

        private static async Task SendAsync(HttpContext context, string type, string key, string value)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Accel-Buffering"] = "no";
            }
            await context.Response.WriteAsync($"event: {type}\n", context.RequestAborted);
            foreach (var line in value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                await context.Response.WriteAsync($"data: {key} {line}\n", context.RequestAborted);
            }

            await context.Response.WriteAsync("\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
}
