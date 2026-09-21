using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile.Pages.Profile;

public sealed class ProfilePageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/profile");
        Summary("Renders the Profile page");
        Tags("Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var tab = HttpContext.Request.Query["tab"].FirstOrDefault() is { Length: > 0 } t ? t : "profile";
        var enroll = HttpContext.Request.Query["enroll"].ToString();
        var langSaved = HttpContext.Request.Query["lang"].ToString() == "saved";
        return Task.FromResult<IResult>(ProfileSlice.Create(ProfilePageModel.From(store, "/profile", tab, enroll, langSaved)));
    }
}
