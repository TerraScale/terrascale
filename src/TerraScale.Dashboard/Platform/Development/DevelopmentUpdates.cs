using System.Reflection.Metadata;
using TerraScale.Dashboard.Platform.Assets;
using TerraScale.Dashboard.Platform.Datastar;

[assembly: MetadataUpdateHandler(typeof(TerraScale.Dashboard.Platform.Development.DevelopmentUpdates))]

namespace TerraScale.Dashboard.Platform.Development;

// These values describe the dev process/compiler, not persisted product state.
public static class DevelopmentUpdates
{
    private static long revision;
    private static readonly string Boot = Guid.NewGuid().ToString("N");

    public static void ClearCache(Type[]? updatedTypes) { }
    public static void UpdateApplication(Type[]? updatedTypes) => Interlocked.Increment(ref revision);

    public static void MapDevelopmentUpdates(this WebApplication app)
    {
        // ASP.NET's hosting environment is the standard development lifecycle,
        // not a configurable product feature or an authentication bypass.
        if (!app.Environment.IsDevelopment()) return;
        app.MapGet("/__dev/events", async (HttpContext context, AssetUrls assets) =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                do
                {
                    await DatastarEvents.PatchSignalsAsync(context, new
                    {
                        _devBoot = Boot,
                        _devRevision = Interlocked.Read(ref revision),
                        _devCss = assets.Version("css/terrascale.css"),
                        _devJs = assets.Version("js/datastar.js") + assets.Version("js/button-loading.js") + assets.Version("js/code-editor.js")
                    });
                } while (await timer.WaitForNextTickAsync(context.RequestAborted));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
    }
}
