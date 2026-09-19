using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

/// <summary>Port of the backup policy form: full/incremental cron schedules,
/// retention days, and the disabled switch (ConfigureBackupPolicyRequest).</summary>
public sealed class SaveBackupPolicyEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/settings/backup-policy");
        Summary("Saves the database backup policy");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var databaseId = form["databaseId"].ToString();
        if (store.FindDatabase(databaseId) is not null
            && int.TryParse(form["retentionDays"].ToString(), out var retentionDays))
        {
            store.SaveSettingsBackupPolicy(
                databaseId,
                form["fullBackupCron"].ToString().Trim(),
                form["incrementalBackupCron"].ToString().Trim(),
                Math.Clamp(retentionDays, 1, 35),
                form["disabled"].Count > 0);
        }

        return Results.Redirect($"/settings?tab=backups&database={databaseId}", false, false);
    }
}
