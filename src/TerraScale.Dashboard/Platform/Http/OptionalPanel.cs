using Microsoft.AspNetCore.Routing;

namespace TerraScale.Dashboard.Platform.Http
{
    public static class OptionalPanel
    {
        public static async Task<T> LoadAsync<T>(HttpContext http, string panel, Func<Task<T>> load, Func<T> unavailable)
        {
            try
            {
                return await load();
            }
            catch (Exception error) when (!http.RequestAborted.IsCancellationRequested)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(OptionalPanel))
                    .DashboardPanelFailed(panel, (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "[unmatched]", ExceptionFrames.Describe(error));
                return unavailable();
            }
        }

        public static Task<bool> RunAsync(HttpContext http, string panel, Func<Task> run)
        {
            return LoadAsync(http, panel, async () => { await run(); return true; }, () => false);
        }
    }
}
