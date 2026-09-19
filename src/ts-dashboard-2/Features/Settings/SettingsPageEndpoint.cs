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
        var query = HttpContext.Request.Query;
        var tab = query["tab"].FirstOrDefault() is { Length: > 0 } t ? t : "identity";
        var toast = query["toast"].ToString() is { Length: > 0 } toastValue ? toastValue : null;
        var rotated = query["rotated"].Count > 0;
        return Task.FromResult<IResult>(SettingsSlice.Create(SettingsPageModel.From(store, "/settings", tab, toast, rotated)));
    }
}
