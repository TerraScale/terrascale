using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

/// <summary>Port of the settings backup policy form: stores schedule and
/// retention per database.</summary>
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
        if (store.FindDatabase(databaseId) is not null)
        {
            store.SaveBackupPolicy(databaseId, form["schedule"].ToString(), form["retention"].ToString());
        }

        return Results.Redirect("/settings?tab=backups", false, false);
    }
}
