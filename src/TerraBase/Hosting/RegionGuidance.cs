using TerraBase.Features.Management;
using TerraBase.Features.Regions;

namespace TerraBase.Hosting;

/// <summary>A valid database-scoped pin awaiting an authorized placement check.</summary>
public sealed record RequestedRegion(string Region);

/// <summary>Recovery information for requests received in another region.</summary>
public static class RegionGuidance
{
    /// <summary>Public recovery documentation.</summary>
    public const string Docs = "https://docs.terrascale.tech/guides/errors";

    /// <summary>The HTTP regional host contract.</summary>
    public static string Host(string region) => region + ".api.terrascale.tech";

    /// <summary>Recovery instructions, shared by every endpoint.</summary>
    public static string Message(string region, IEnumerable<string>? regions = null) =>
        $"wrong_region: {(regions is null ? "request reached another region" : "database regions: " + string.Join(", ", regions))}; "
        + $"use https://{Host(region)} with the matching region pin. PostgreSQL TLS uses pg.terrascale.tech:5432, "
        + $"with that regional host's address and the pg.terrascale.tech TLS name; retrying the same address will not change regions. See {Docs}";

    /// <summary>Placement, expanding allRegions through current membership.</summary>
    public static async ValueTask<IReadOnlyList<string>> RegionsAsync(ManagedDatabase database, string region, IRegionCatalog catalog, CancellationToken cancellationToken) =>
        database.AllRegions ? await catalog.ServingAsync(cancellationToken).ConfigureAwait(false) : database.Regions(region);

    /// <summary>A non-cacheable HTTP refusal with optional authorized placement.</summary>
    public static Task RefuseAsync(HttpContext context, string target, IReadOnlyList<string>? regions = null, string code = "wrong_region")
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new ErrorBody(new ErrorDetail(code, Message(target, regions), Regions: regions, Host: Host(target), Docs: Docs)),
            TerraBaseJsonContext.Default.ErrorBody, statusCode: StatusCodes.Status421MisdirectedRequest).ExecuteAsync(context);
    }
}
