using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

internal sealed class GetRestoreOperationEndpoint(IManagementService management, IManagedBackupService backups) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/restores/{operation}");
        Name("getRestoreOperation");
        Summary("Reads a background restore operation");
        Description("The status of a restore started by POST …/backups/{backup}/restore or POST …/point-in-time/restore, which answer 202 with the operation's id. status is running while the copy runs, then completed, with database naming the restored database, or failed with error and message (interrupted when the process stopped while it ran). Unknown ids, and ids of another project's or database's operations, answer 404. " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<RestoreOperation>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await backups.GetRestoreOperationAsync(database.OrganizationId, database.DatabaseId!, HttpContext.RouteValue("operation"), cancellationToken) switch
            {
                RestoreOperationRecord record => TypedResults.Ok(RestoreOperationRoute.ToBody(record)),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
