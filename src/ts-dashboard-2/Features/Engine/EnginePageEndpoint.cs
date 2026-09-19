using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Engine;

public sealed class EnginePageEndpoint(DashboardStore store, TerraBaseBackend terrabase) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/engine");
        Summary("Renders the Engine page");
        Tags("Pages");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var shell = ShellFactory.Create(store, "/engine");
        var model = await EnginePageModel.LoadAsync(terrabase, shell, ct);
        return EngineSlice.Create(model);
    }
}
