using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;

namespace TerraBase.Features.Import;

/// <summary>The destination an import route addresses, resolved for its project token and served here as its home.</summary>
/// <param name="Database">The destination.</param>
/// <param name="Name">Its name.</param>
public sealed record ImportDestination(TenantContext Database, string Name);

/// <summary>A <c>421 wrong_region</c> answer naming the destination's home.</summary>
/// <param name="Result">The answer.</param>
public sealed record ImportElsewhere(IResult Result);

/// <summary>The destination, or why the route cannot serve it here.</summary>
public readonly union ImportDestinationResult(ImportDestination, ImportElsewhere, NotFound, Unavailable);

/// <summary>Routes and destination resolution of the import API (docs/terrabase/import.md section 2).</summary>
public static class ImportRoute
{
    /// <summary>The import route of a destination.</summary>
    public const string Path = "/v1/projects/{project}/databases/{database}/import";

    /// <summary>The OpenAPI tag of every import operation.</summary>
    public const string Tag = "Imports";

    /// <summary>The sentence every import operation description ends with.</summary>
    public const string Reference = " Contract: docs/terrabase/import.md. Only ts-auth project access tokens; send every import call to the destination's home region (421 wrong_region elsewhere).";

    private static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    /// <summary>Resolves the route's database for the request's project token; only its home region serves imports.</summary>
    public static async ValueTask<ImportDestinationResult> ResolveAsync(HttpContext context, ITenantDirectory tenants, TerraBaseOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(options);
        if (context.Features.Get<ProjectAccess>() is not { } access
            // Project middleware pins this route to the admitted database's immutable identity.
            || context.Request.RouteValues["database"] is not string target)
        {
            return UnknownDatabase;
        }

        var found = await tenants.FindDatabaseAsync(access.OrganizationId, access.ProjectId, target, cancellationToken).ConfigureAwait(false);
        if (found is not TenantContext database || (access.DatabaseName is not null && access.DatabaseId != database.DatabaseId))
        {
            return found switch
            {
                Unavailable error => error,
                _ => UnknownDatabase,
            };
        }

        return await tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false) switch
        {
            TenantDatabaseRecord record when record.Placement.Home != options.Region =>
                new ImportElsewhere(DatabaseRegions.HomeRegion(record.Placement.Home, record.Placement.Regions)),
            // Usage metering attributes the request (and an upload's bytes) to the destination, as for document routes.
            TenantDatabaseRecord record => Attribute(context, new ImportDestination(database, record.Name)),
            NotFound => UnknownDatabase,
            Unavailable error => error,
        };
    }

    private static ImportDestination Attribute(HttpContext context, ImportDestination destination)
    {
        context.Features.Set(destination.Database);
        return destination;
    }

    /// <summary>The HTTP answer for a job result: <paramref name="status"/> with the job, or the failure's error.</summary>
    public static IResult Answer(ImportResult result, int status) => result switch
    {
        ImportJobBody job when status == StatusCodes.Status202Accepted => TypedResults.Accepted((string?)null, job),
        ImportJobBody job => TypedResults.Ok(job),
        NotFound error => ErrorResponse.From(error),
        Conflict error => ErrorResponse.From(error),
        Validation error => ErrorResponse.From(error),
        PayloadTooLarge error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };

    /// <summary>Resolves the destination, then answers with <paramref name="run"/>'s result.</summary>
    public static async ValueTask<IResult> RunAsync(
        HttpContext context, ITenantDirectory tenants, TerraBaseOptions options, int status, Func<ImportDestination, ValueTask<ImportResult>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return await ResolveAsync(context, tenants, options, context.RequestAborted).ConfigureAwait(false) switch
        {
            ImportDestination destination => Answer(await run(destination).ConfigureAwait(false), status),
            ImportElsewhere elsewhere => elsewhere.Result,
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
