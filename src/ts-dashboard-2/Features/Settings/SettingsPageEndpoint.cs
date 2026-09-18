using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

public sealed class SettingsPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/settings");
        Summary("Renders the Settings page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var tab = HttpContext.Request.Query["tab"].FirstOrDefault() is { Length: > 0 } t ? t : "identity";
        return Task.FromResult<IResult>(SettingsSlice.Create(SettingsPageModel.From(store, "/settings", tab)));
    }
}
