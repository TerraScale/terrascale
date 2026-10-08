using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>GET …/databases/{database}/backup-policy</c>: reads the backup policy of a database.</summary>
public sealed class GetBackupPolicyEndpoint(IManagementService management, IManagedBackupService backups) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/backup-policy");
        Name("getBackupPolicy");
        Summary("Reads the backup policy of a database");
        Description("Until a policy is saved this returns the default: a full backup daily at 02:00 UTC, kept 7 days, with updatedAt null. The scheduler applies this default from the database's creation, as it applies a saved policy from its save." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<BackupPolicy>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
            ? TypedResults.Ok(await backups.GetPolicyAsync(database.DatabaseId!, cancellationToken))
            : ErrorResponse.From(new NotFound());
}
