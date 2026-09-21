using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

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
        var payment = query["payment"].FirstOrDefault();
        var paymentStatus = payment is "success" or "canceled" ? payment : null;
        var activeStep = OrganizationsNewPageModel.ResolveStep(query["step"].FirstOrDefault(), paymentStatus);
        return Task.FromResult<IResult>(OrganizationsNewSlice.Create(
            OrganizationsNewPageModel.From(store, "/organizations/new", activeStep, paymentStatus)));
    }
}
