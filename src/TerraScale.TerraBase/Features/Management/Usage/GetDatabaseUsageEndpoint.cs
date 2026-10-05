using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Usage;

/// <summary><c>GET …/databases/{database}/usage</c>: reads the storage and operation usage of a database.</summary>
public sealed class GetDatabaseUsageEndpoint(IManagementService management, IUsageService usage) : EndpointWithoutRequest<IResult>
{
    /// <summary>What the read and write counts guarantee, for every usage route.</summary>
    internal const string CountsDescription =
        "Reads and writes are best-effort dashboard usage, not billing data: they are saved every 10 seconds and at a clean shutdown, "
        + "a failed save is retried, and a crash loses the operations counted since the last successful save.";

    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/usage");
        Name("getDatabaseUsage");
        Summary("Reads the usage of a database");
        Description("Bytes on disk, collections, live documents, and document reads (gets, list and query pages) and writes (committed puts, patches and deletes, each operation of a transaction) since the database was created. " + CountsDescription + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<DatabaseUsage>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
                && await usage.GetDatabaseUsageAsync(database, cancellationToken) is { } result
                ? TypedResults.Ok(result)
                : TypedResults.NotFound();
        }
        catch (QueryCapacityException)
        {
            return TypedResults.BadRequest("query too expensive");
        }
    }
}
