using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

public sealed class OrganizationPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/organization");
        Summary("Renders the Organization page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(OrganizationSlice.Create(OrganizationPageModel.From(store, "/organization")));
}
