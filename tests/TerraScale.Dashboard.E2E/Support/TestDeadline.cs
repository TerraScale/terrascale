using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E
{
    // Fast runs every journey in parallel, often beside other builds on the same host, so a signal that takes
    // milliseconds on an idle machine can take tens of seconds. Waits end on the real signal; this deadline only
    // bounds a genuine hang.
    internal static class TestDeadline
    {
        public static readonly TimeSpan Signal = TimeSpan.FromMinutes(2);

        public static CancellationTokenSource Start()
        {
            return new(Signal);
        }

        // Completes once the host's state schema is initialized, at startup or on a background retry.
        public static Task StateInitializedAsync(WebApplication app)
        {
            return app.Services.GetRequiredService<DashboardStateInitializer>().WhenInitialized.WaitAsync(Signal);
        }
    }
}
