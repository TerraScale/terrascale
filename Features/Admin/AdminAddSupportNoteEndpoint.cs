using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed class AdminAddSupportNoteEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/admin/users/support-note");
        Summary("Adds a support note to an admin user");
        Tags("Admin");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.AdminAddSupportNote(form["userId"].ToString(), form["note"].ToString());
        return Results.Redirect("/admin/users", false, false);
    }
}
