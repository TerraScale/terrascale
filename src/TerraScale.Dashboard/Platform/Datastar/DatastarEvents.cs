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
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(html, context.RequestAborted);
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
