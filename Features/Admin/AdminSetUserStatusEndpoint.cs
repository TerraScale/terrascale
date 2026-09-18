using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed class AdminSetUserStatusEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/admin/users/status");
        Summary("Enables or disables an admin user");
        Tags("Admin");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.AdminSetUserEnabled(form["userId"].ToString(), form["enabled"] == "true");
        return Results.Redirect("/admin/users", false, false);
    }
}
