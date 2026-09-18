using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed class AdminSetUserRoleEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/admin/users/role");
        Summary("Changes an admin user between owner and user");
        Tags("Admin");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.AdminSetUserRole(form["userId"].ToString(), form["role"].ToString());
        return Results.Redirect("/admin/users", false, false);
    }
}
