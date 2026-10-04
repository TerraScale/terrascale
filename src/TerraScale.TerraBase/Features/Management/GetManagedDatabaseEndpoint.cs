using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET …/databases/{database}</c>: reads a database.</summary>
public sealed class GetManagedDatabaseEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Database);
        Name("getManagedDatabase");
        Summary("Reads a database");
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabase>();
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.Management);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.GetDatabaseAsync(ManagementRoute.Value(HttpContext, "organization"), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), cancellationToken) is { } database
            ? TypedResults.Ok(database)
            : TypedResults.NotFound();
}
