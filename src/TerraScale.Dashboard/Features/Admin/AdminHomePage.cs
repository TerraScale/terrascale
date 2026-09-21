using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin;

/// <summary>/admin redirects to the users inventory, mirroring app/admin/page.tsx.</summary>
public sealed class AdminHomePageEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/admin");
        Summary("Redirects to the admin users page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Redirect("/admin/users", false, false));
}
