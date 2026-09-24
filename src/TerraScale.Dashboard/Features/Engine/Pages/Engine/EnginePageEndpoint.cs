using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Engine.Pages.Engine;

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
        var regionsPage = ReadPage("regionsPage", "regionPage");
        var metricsPage = ReadPage("metricsPage", "metricPage");
        var model = await EnginePageModel.LoadAsync(terrabase, shell, ct, regionsPage, metricsPage);
        return EngineSlice.Create(model);
    }

    private int ReadPage(string pluralName, string singularName)
    {
        var value = HttpContext.Request.Query[pluralName].FirstOrDefault()
            ?? HttpContext.Request.Query[singularName].FirstOrDefault();
        return int.TryParse(value, out var page) ? page : 1;
    }
}
