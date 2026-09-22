using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Settings.Endpoints;

public sealed class SaveAuditRetentionEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/settings/audit-retention");
        Summary("Saves the audit retention window");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        if (int.TryParse(form["retentionDays"], out var days) && days is >= 1 and <= 90)
        {
            store.AuditRetentionDays = days;
        }

        return Results.Redirect("/settings?tab=audit", false, false);
    }
}
