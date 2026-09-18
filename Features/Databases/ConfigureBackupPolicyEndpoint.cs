using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed class ConfigureBackupPolicyEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/databases/backup-policy");
        Summary("Enables or disables scheduled backups for a database");
        Tags("Databases");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var databaseId = form["databaseId"].ToString();
        store.SetBackupsEnabled(databaseId, form["enabled"] == "true");
        return Results.Redirect("/databases/" + databaseId, false, false);
    }
}
