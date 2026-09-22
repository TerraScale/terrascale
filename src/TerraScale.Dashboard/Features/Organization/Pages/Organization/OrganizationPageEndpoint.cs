using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization.Pages.Organization;

public sealed class OrganizationPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/organization");
        Summary("Renders the Organization page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var payment = query["payment"].FirstOrDefault();
        var paymentStatus = payment is "success" or "canceled" ? payment : null;
        var wizard = NewOrganizationWizardModel.From(
            store,
            query["newOrganization"].FirstOrDefault(),
            paymentStatus,
            query["error"].FirstOrDefault());
        return Task.FromResult<IResult>(OrganizationSlice.Create(
            OrganizationPageModel.From(store, "/organization", wizard)));
    }
}
