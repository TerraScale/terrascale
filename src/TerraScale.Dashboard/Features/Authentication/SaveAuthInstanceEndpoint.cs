using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Authentication;

public sealed class SaveAuthInstanceEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/auth-management/instance");
        Summary("Saves ts-auth hosted UI branding");
        Tags("Authentication");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var name = form["name"].ToString();
        var logo = form["logo_url"].ToString();
        var color = form["theme_color"].ToString();
        if (string.IsNullOrWhiteSpace(name))
            return Results.Redirect("/auth/settings?error=Instance%20name%20is%20required.");
        if (logo.Length > 0 && (!Uri.TryCreate(logo, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            return Results.Redirect("/auth/settings?error=" + Uri.EscapeDataString("Logo URL must be an absolute HTTP(S) URL."), false, false);
        if ((color.Length != 7 || color[0] != '#' || !color[1..].All(char.IsAsciiHexDigit)))
            return Results.Redirect("/auth/settings?error=" + Uri.EscapeDataString("Theme color must be a hex color such as #3B82F6."), false, false);
        store.SaveAuthBranding(name, logo, color);
        return Results.Redirect("/auth/settings?toast=" + Uri.EscapeDataString("Instance settings saved."), false, false);
    }
}
