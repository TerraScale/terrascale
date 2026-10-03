using TerraScale.Endpoints;

namespace TerraScale.Auth.Features.Assets;

/// <summary>Brand, stylesheet, script and font assets compiled into the binary.</summary>
public static class EmbeddedAssets
{
    private static readonly Dictionary<string, (string Resource, string ContentType)> Admin = new(StringComparer.Ordinal)
    {
        ["admin.css"] = ("assets/admin/admin.css", "text/css; charset=utf-8"),
        ["datastar.js"] = ("assets/admin/datastar.js", "text/javascript; charset=utf-8"),
        ["logo.svg"] = ("assets/admin/logo.svg", "image/svg+xml"),
    };

    private static readonly Dictionary<string, (string Resource, string ContentType)> Fonts = new(StringComparer.Ordinal)
    {
        ["oxanium-latin-wght-normal.woff2"] = ("assets/fonts/oxanium-latin-wght-normal.woff2", "font/woff2"),
        ["jetbrains-mono-latin-wght-normal.woff2"] = ("assets/fonts/jetbrains-mono-latin-wght-normal.woff2", "font/woff2"),
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
