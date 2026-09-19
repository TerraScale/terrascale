using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

/// <summary>Port of the settings on-demand backup action: starts a backup
/// job for the selected database immediately.</summary>
public sealed class StartOnDemandBackupEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/settings/backup-now");
        Summary("Starts an on-demand backup for a database");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var databaseId = form["databaseId"].ToString();
        store.StartOnDemandBackup(databaseId);
        return Results.Redirect($"/settings?tab=backups&database={databaseId}", false, false);
    }
}
