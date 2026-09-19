using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

public sealed class UpdateLanguagePreferenceEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/language");
        Summary("Saves the interface language preference");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.UpdateLocale(form["locale"].ToString());
        return Results.Redirect("/profile?tab=profile&lang=saved", false, false);
    }
}
