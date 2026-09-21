using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Settings;

public sealed class UpdateAuthenticationSettingEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/settings/authentication");
        Summary("Toggles TerraScale Authentication for the project");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.SetAuthenticationEnabled(form["enabled"].Count > 0);
        return Results.Redirect("/settings?tab=identity", false, false);
    }
}
