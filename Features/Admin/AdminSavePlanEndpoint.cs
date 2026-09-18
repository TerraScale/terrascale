using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed class AdminSavePlanEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/admin/plans/save");
        Summary("Creates or updates a pricing plan");
        Tags("Admin");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var planId = form["planId"].ToString();
        var editing = planId.Length > 0 && store.AdminPlans.Any(p => p.PlanId == planId);

        var readQps = Int(form["readQps"].ToString());
        var bandwidth = Long(form["directReadBandwidth"].ToString());
        var views = Int(form["activeViews"].ToString());
        var sockets = Int(form["websocketConnections"].ToString());
        var hooks = Int(form["webhookEndpoints"].ToString());
        (int, long, int, int, int)? guards = readQps is { } q && bandwidth is { } b && views is { } v && sockets is { } s && hooks is { } w
            ? (q, b, v, s, w)
            : null;

        var plan = new DashboardStore.AdminPlanRow(
            PlanId: editing ? planId : "plan_" + form["planKey"].ToString(),
            Key: form["planKey"].ToString(),
            Name: form["planName"].ToString(),
            MonthlyBaseCents: Long(form["monthlyBaseCents"].ToString()) ?? 0,
            Currency: "USD",
            Visible: !editing || store.AdminPlans.First(p => p.PlanId == planId).Visible,
            MaxDatabases: Int(form["maxDatabases"].ToString()) ?? 0,
            StorageOverageCentsPerGbRegion: Long(form["storageOverageCents"].ToString()),
            Guards: guards);
        store.AdminSavePlan(plan, editing);
        return Results.Redirect("/admin/pricing?notice=" + Uri.EscapeDataString(editing ? "Pricing plan updated." : "Pricing plan created."), false, false);
    }

    private static long? Long(string value) =>
        long.TryParse(value, out var parsed) && parsed >= 0 ? parsed : null;

    private static int? Int(string value) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : null;
}
