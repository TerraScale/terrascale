using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Backups;

/// <summary>Confirm step: consumes the single-use confirmation token and
/// records the restore job. Expired or unknown tokens are rejected.</summary>
public sealed class ExecuteRestoreEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/backups/restore-execute");
        Summary("Executes a restore plan after confirmation");
        Tags("Backups");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var token = form["token"].ToString();
        var error = store.ExecuteRestore(token, out var job);
        return Results.Redirect(error is null && job is not null
            ? "/backups?restored=" + Uri.EscapeDataString(job.Id)
            : "/backups?error=" + Uri.EscapeDataString(error ?? "The restore request failed."), false, false);
    }
}
