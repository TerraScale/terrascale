using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

internal sealed class GetBackupPolicyEndpoint(IManagementService management, IManagedBackupService backups) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/backup-policy");
        Name("getBackupPolicy");
        Summary("Reads the backup policy of a database");
        Description("Until a policy is saved this returns the default: a full backup daily at 02:00 UTC, kept 7 days, with updatedAt null. The scheduler applies this default from the database's creation, as it applies a saved policy from its save. lastSucceededAt, lastFailedAt and lastError report the newest scheduled attempt's outcome, and nextDueAt the next cron occurrence; nextDueAt is null with no cron." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<BackupPolicyStatus>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await backups.GetPolicyAsync(database.DatabaseId!, cancellationToken) switch
            {
                BackupPolicyStatus policy => TypedResults.Ok(policy),
                Unavailable error => ErrorResponse.From(error),
            },
            NotFound => ErrorResponse.From(new NotFound()),
            Unavailable error => ErrorResponse.From(error),
        };
}
