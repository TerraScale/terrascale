using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile;

public sealed class UpdateProfileEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile");
        Summary("Updates the account display name and phone number");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.UpdateProfile(form["name"].ToString(), form["phoneNumber"].ToString());
        return Results.Redirect("/profile", false, false);
    }
}
