using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Platform;

/// <summary><c>GET /v1/platform/databases</c>: the platform databases, for platform admins.</summary>
internal sealed class ListPlatformDatabasesEndpoint(IManagementService management, TerraBaseOptions options, ILogger<ListPlatformDatabasesEndpoint> logger)
    : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(PlatformExplore.Databases);
        Name("listPlatformDatabases");
        Summary("Lists the platform databases");
        Description("Platform admins only: a ts-auth user access token with platform_role admin (403 otherwise). Read-only.");
        Tags("Platform");
        Produces<PlatformDatabaseListBody>();
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(PlatformExplore.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var items = new List<PlatformDatabaseBody>();
        foreach (var (name, database) in PlatformDatabases.Databases)
        {
            switch (await management.FindDatabaseRecordAsync(PlatformDatabases.OrganizationId, database.ProjectId, name, cancellationToken))
            {
                case ManagedDatabase record:
                    items.Add(new PlatformDatabaseBody(record.Name, record.Id, record.Home(options.Region), record.Replicas, record.AllRegions));
                    break;
                case Unavailable error:
                    return ErrorResponse.From(error);
                case NotFound:
                    // Not seeded yet: listed once it is.
                    break;
            }
        }

        PlatformExplore.Audit(logger, HttpContext, "*", null, null);
        return TypedResults.Ok(new PlatformDatabaseListBody(items));
    }
}
