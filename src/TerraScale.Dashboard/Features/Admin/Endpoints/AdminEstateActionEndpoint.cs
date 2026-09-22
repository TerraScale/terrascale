using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Endpoints;

/// <summary>Records an audited product action (pause, refund, view-as, …).
/// Product controls never perform infrastructure operations — they append to
/// the audit trail, mirroring runAdminEstateAction.</summary>
public sealed class AdminEstateActionEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/admin/estate/action");
        Summary("Records an audited admin product action");
        Tags("Admin");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.AdminRecordEstateAction(
            form["action"].ToString(),
            form["targetId"].ToString(),
            form["reason"].ToString(),
            store.CurrentUser.Email);
        return Results.Redirect("/admin/estate?notice=" + Uri.EscapeDataString(DashboardStore.EstateActionNotice), false, false);
    }
}
