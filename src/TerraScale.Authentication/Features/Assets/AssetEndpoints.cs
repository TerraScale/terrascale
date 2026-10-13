using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Assets;

/// <summary>Brand, stylesheet, script and font assets compiled into the binary.</summary>
public static class EmbeddedAssets
{
    private static readonly Dictionary<string, (string Resource, string ContentType)> Admin = new(StringComparer.Ordinal)
    {
        ["datastar.js"] = ("assets/admin/datastar.js", "text/javascript; charset=utf-8"),
        // ts-resources' horizontal TerraScale logo (blue, and blue with a light wordmark for dark backgrounds) and cube mark,
        // with inline fills in place of the source files' <style> blocks, which the asset CSP (style-src 'none') covers.
        ["logo.svg"] = ("assets/admin/logo.svg", "image/svg+xml"),
        ["logo-dark.svg"] = ("assets/admin/logo-dark.svg", "image/svg+xml"),
        ["icon.svg"] = ("assets/admin/icon.svg", "image/svg+xml"),
        // The landing hero's dotted world map (one currentColor path): the hosted pages' background, used as a CSS mask.
        ["world-map-dots.svg"] = ("assets/admin/world-map-dots.svg", "image/svg+xml"),
        // Email clients do not show SVG; these are the dashboard's horizontal logos rasterized at 2x.
        ["email-logo.png"] = ("assets/email/logo.png", "image/png"),
        ["email-logo-dark.png"] = ("assets/email/logo-dark.png", "image/png"),
    };

    private static readonly Dictionary<string, (string Resource, string ContentType)> Fonts = new(StringComparer.Ordinal)
    {
        ["oxanium-latin-wght-normal.woff2"] = ("assets/fonts/oxanium-latin-wght-normal.woff2", "font/woff2"),
        ["Geist-Variable.woff2"] = ("assets/fonts/Geist-Variable.woff2", "font/woff2"),
        ["GeistMono-Variable.woff2"] = ("assets/fonts/GeistMono-Variable.woff2", "font/woff2"),
    };

    /// <summary>Serves an admin asset by exact name.</summary>
    public static IResult AdminAsset(HttpContext context, string name) => Serve(context, Admin, name);

    /// <summary>Serves a font asset by exact name.</summary>
    public static IResult FontAsset(HttpContext context, string name) => Serve(context, Fonts, name);

    private static IResult Serve(HttpContext context, Dictionary<string, (string Resource, string ContentType)> catalog, string name)
    {
        if (!catalog.TryGetValue(name, out var asset)
            || typeof(EmbeddedAssets).Assembly.GetManifestResourceStream(asset.Resource) is not { } stream)
        {
            return TypedResults.Text("Not found", "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
        }

        var headers = context.Response.Headers;
        headers.CacheControl = "public, max-age=3600";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'none'; script-src 'none'; sandbox";
        return TypedResults.Stream(stream, asset.ContentType);
    }
}

/// <summary><c>GET /admin/{asset}</c>: bundled administrator assets.</summary>
public sealed class GetAdminAssetEndpoint : IEndpoint
{
    /// <inheritdoc />
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/admin/{asset}", (HttpContext context, string asset) => EmbeddedAssets.AdminAsset(context, asset))
            .WithName("admin_asset")
            .WithSummary("Load bundled administrator assets")
            .WithTags("Admin")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .AllowAnonymous();
}

/// <summary><c>GET /fonts/{asset}</c>: bundled web fonts.</summary>
public sealed class GetFontAssetEndpoint : IEndpoint
{
    /// <inheritdoc />
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/fonts/{asset}", (HttpContext context, string asset) => EmbeddedAssets.FontAsset(context, asset))
            .WithName("font_asset")
            .WithSummary("Load bundled fonts")
            .WithTags("Admin")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .AllowAnonymous();
}
