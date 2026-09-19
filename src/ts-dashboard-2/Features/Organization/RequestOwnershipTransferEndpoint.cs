using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

/// <summary>Port of requestOwnershipTransferAction: records the pending
/// transfer (v1 emails the confirmation code; the demo store only tracks the
/// expiry).</summary>
public sealed class RequestOwnershipTransferEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/members/ownership/request");
        Summary("Requests an ownership transfer confirmation");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RequestOwnershipTransfer(form["userId"].ToString());
        return Results.Redirect("/organization", false, false);
    }
}
