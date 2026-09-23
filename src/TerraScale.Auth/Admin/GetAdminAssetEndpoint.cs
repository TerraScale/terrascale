namespace TerraScale.Auth.Admin;

using TerraScale.Endpoints;

/// <summary>Serves the dashboard-derived admin assets from the single-file executable.</summary>
internal sealed class GetAdminAssetEndpoint : IEndpoint
{
    private static readonly IReadOnlyDictionary<string, (string Resource, string ContentType)> AdminAssets =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["admin.css"] = ("TerraScale.Auth.Admin.Assets.admin.css", "text/css; charset=utf-8"),
            ["datastar.js"] = ("TerraScale.Auth.Admin.Assets.datastar.js", "text/javascript; charset=utf-8"),
            ["logo.svg"] = ("TerraScale.Auth.Admin.Assets.logo.svg", "image/svg+xml"),
        };

    private static readonly IReadOnlyDictionary<string, (string Resource, string ContentType)> FontAssets =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["oxanium-latin-wght-normal.woff2"] = ("TerraScale.Auth.Admin.Assets.oxanium.woff2", "font/woff2"),
            ["jetbrains-mono-latin-wght-normal.woff2"] = ("TerraScale.Auth.Admin.Assets.jetbrains-mono.woff2", "font/woff2"),
        };

    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/fonts/{asset}", (string asset, HttpResponse response) => Serve(asset, FontAssets, response));
        return routes.MapGet("/admin/{asset}", (string asset, HttpResponse response) => Serve(asset, AdminAssets, response));
    }

    private static IResult Serve(string asset, IReadOnlyDictionary<string, (string Resource, string ContentType)> assets, HttpResponse response)
    {
        if (!assets.TryGetValue(asset, out var metadata))
            return Results.NotFound();
        var stream = typeof(GetAdminAssetEndpoint).Assembly.GetManifestResourceStream(metadata.Resource);
        if (stream is null)
            return Results.NotFound();
        response.Headers.CacheControl = "public, max-age=3600";
        return Results.File(stream, metadata.ContentType);
    }
}
