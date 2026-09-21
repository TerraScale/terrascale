using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>Port of switchOrganizationAction: v1 assigns the org cookie and
/// reloads "/". The demo store is single-org, so this records the intent and
/// returns to the dashboard root.</summary>
public sealed class SwitchOrganizationEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/switch");
        Summary("Switches the active organization");
        Tags("Organization");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Redirect("/", false, false));
}
