using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class SetDatabaseTtlEndpoint(IManagementService management, TerraBaseOptions options) : Endpoint<DatabaseTtlRequest, IResult>
{
    public override void Configure()
    {
        Put(ProjectApiRoute.Database + "/ttl");
        Metadata(AnchoredRevocation.Instance);
        Name("setDatabaseTtl");
        Summary("Deletes a database after a time");
        Description("Sets expiresAt to now plus ttlSeconds, replacing any earlier TTL." + DatabaseTtl.Description + " 409 move_in_progress while the database's writer is moving; send the request again after it settles. Sent to the database's home region (421 wrong_region elsewhere). " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    public override async ValueTask<IResult> HandleAsync(DatabaseTtlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TtlSeconds is null)
        {
            return ErrorResponse.From(new Validation("ttlSeconds is required"));
        }

        return DatabaseTtl.Invalid(request.TtlSeconds) is { } invalid
            ? ErrorResponse.From(invalid)
            : await DatabaseTtlAnswer.SetAsync(HttpContext, management, options.Region, request.TtlSeconds, cancellationToken);
    }
}

internal sealed class ClearDatabaseTtlEndpoint(IManagementService management, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ProjectApiRoute.Database + "/ttl");
        Metadata(AnchoredRevocation.Instance);
        Name("clearDatabaseTtl");
        Summary("Keeps a database until it is deleted");
        Description("Removes the database's TTL; expiresAt becomes null. 409 move_in_progress while the database's writer is moving; send the request again after it settles. Sent to the database's home region (421 wrong_region elsewhere). " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DatabaseTtlAnswer.SetAsync(HttpContext, management, options.Region, null, cancellationToken);
}

internal static class DatabaseTtlAnswer
{
    public static async ValueTask<IResult> SetAsync(HttpContext context, IManagementService management, string region, long? ttlSeconds, CancellationToken cancellationToken)
    {
        var placement = await DatabaseRegions.ElsewhereAsync(context, management, region).ConfigureAwait(false);
        return placement switch
        {
            Done => From(context, await management.SetDatabaseTtlAsync(ManagementRoute.UserId(context), ManagementRoute.OrganizationId(context), context.RouteValue("project"),
                context.RouteValue("database"), ttlSeconds, cancellationToken).ConfigureAwait(false)),
            DatabaseRegions.Misdirected elsewhere => elsewhere,
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    private static IResult From(HttpContext context, SetDatabaseRegionsResult result) => result switch
    {
        ManagedDatabase value => TypedResults.Ok(ManagedDatabaseBody.From(value, Listeners.RegionOf(context))),
        NotFound error => ManagementRoute.DatabaseNotFound(error),
        Forbidden error => ErrorResponse.From(error),
        Conflict error => ErrorResponse.From(error),
        Validation error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };
}
