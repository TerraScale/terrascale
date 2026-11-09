using TerraScale.Dashboard.Platform.Assets;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Platform.Development
{
    public sealed class DevelopmentEventsEndpoint(AssetUrls assets) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/__dev/events");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var context = HttpContext;
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                do
                {
                    await DatastarEvents.PatchSignalsAsync(context, new
                    {
                        _devBoot = DevelopmentUpdates.BootId,
                        _devRevision = DevelopmentUpdates.Revision,
                        _devCss = assets.Version("css/terrascale.css"),
                        _devJs = assets.Version("js/datastar.js") + assets.Version("js/button-loading.js") + assets.Version("js/code-editor.js")
                    });
                } while (await timer.WaitForNextTickAsync(context.RequestAborted));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            return Results.Empty;
        }
    }
}
