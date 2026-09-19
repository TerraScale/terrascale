using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

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
        var step = query["step"].FirstOrDefault() is { Length: > 0 } and var s && int.TryParse(s, out var n) && n is >= 1 and <= 3 ? n : 1;
        var payment = query["payment"].FirstOrDefault();
        var paymentStatus = payment is "success" or "canceled" ? payment : null;
        return Task.FromResult<IResult>(OrganizationsNewSlice.Create(
            OrganizationsNewPageModel.From(store, "/organizations/new", step, paymentStatus)));
    }
}
