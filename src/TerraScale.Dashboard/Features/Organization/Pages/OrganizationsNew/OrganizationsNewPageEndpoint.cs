using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization.Pages.OrganizationsNew;

public sealed class OrganizationsNewPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/organizations/new");
        Summary("Renders the create-organization wizard");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        // v1 resolveNewOrganizationStep: named steps in the URL, default "organization".
        var step = query["step"].FirstOrDefault() switch
        {
            "payment" => 2,
            "database" => 3,
            _ => 1,
        };
        var payment = query["payment"].FirstOrDefault();
        var paymentStatus = payment is "success" or "canceled" ? payment : null;
        return Task.FromResult<IResult>(OrganizationsNewSlice.Create(
            OrganizationsNewPageModel.From(store, "/organizations/new", step, paymentStatus)));
    }
}
