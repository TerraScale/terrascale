namespace TerraScale.DotNet;

/// <summary>How one request's origin is selected (ADR 0050 §5.1/§5.8/§8).</summary>
internal enum TerraScaleRouteKind
{
    /// <summary>The configured product origin; no regional routing.</summary>
    ConfiguredOrigin,

    /// <summary>The Atlas metadata origin itself (the catalog route).</summary>
    DiscoveryOrigin,

    /// <summary>A database-scoped member route; discovery picks the regional endpoint.</summary>
    Database
}

/// <summary>
/// The fixed route classification for one request, mirroring the TS SDK's
/// path table (ADR 0050 §2/§8): database-scoped requests discover; product
/// routes target the configured origin; <c>api/v1/regions</c> exactly is the
/// catalog on the discovery origin; database-less member routes use the API
/// proxy, since discovery requires a database selector.
/// </summary>
internal readonly record struct TerraScaleRequestRouting(TerraScaleRouteKind Kind, string? Database, bool NeedsWrite)
{
    public static TerraScaleRequestRouting Classify(HttpMethod method, string requestUri, TerraScaleDiscoveryRouter? router)
    {
        if (router is null)
        {
            return new TerraScaleRequestRouting(TerraScaleRouteKind.ConfiguredOrigin, null, IsMutation(method));
        }

        var path = requestUri;
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 4 &&
            segments[0] == "api" && segments[1] == "v1" && segments[2] == "databases" &&
            TerraScaleRoutingValidation.IsValidDatabaseName(segments[3]))
        {
            return new TerraScaleRequestRouting(TerraScaleRouteKind.Database, segments[3], IsMutation(method));
        }

        if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "v1")
        {
            // Dashboard-owned product routes: organizations, projects, and
            // region summaries target the product origin.
            if (segments[2] is "organizations" or "projects")
            {
                return new TerraScaleRequestRouting(TerraScaleRouteKind.ConfiguredOrigin, null, IsMutation(method));
            }

            if (segments[2] == "regions")
            {
                // `api/v1/regions` exactly is the region catalog on the
                // discovery origin, never a product route.
                return segments.Length == 3
                    ? new TerraScaleRequestRouting(TerraScaleRouteKind.DiscoveryOrigin, null, false)
                    : new TerraScaleRequestRouting(TerraScaleRouteKind.ConfiguredOrigin, null, false);
            }
        }

        return new TerraScaleRequestRouting(TerraScaleRouteKind.ConfiguredOrigin, null, IsMutation(method));
    }

    private static bool IsMutation(HttpMethod method) =>
        method != HttpMethod.Get &&
        method != HttpMethod.Head &&
        method != HttpMethod.Options &&
        method != HttpMethod.Trace;
}
