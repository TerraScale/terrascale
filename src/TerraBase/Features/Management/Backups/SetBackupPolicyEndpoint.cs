using Cronos;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>PUT …/databases/{database}/backup-policy</c>: saves the backup policy of a database.</summary>
internal sealed class SetBackupPolicyEndpoint(IManagementService management, IManagedBackupService backups) : Endpoint<BackupPolicyRequest, IResult>
{
    private const int MaxRetentionDays = 35;

    /// <inheritdoc />
    public override void Configure()
    {
        Put(ProjectApiRoute.Database + "/backup-policy");
        Metadata(AnchoredRevocation.Instance);
        Name("setBackupPolicy");
        Summary("Saves the backup policy of a database");
        Description("fullBackupCron is a standard 5-field cron expression evaluated in UTC, or null or empty for no scheduled backups. retentionDays (1 to 35) applies to manual and scheduled backups. disabled pauses scheduled backups." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<BackupPolicy>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(BackupPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cron = string.IsNullOrWhiteSpace(request.FullBackupCron) ? null : request.FullBackupCron.Trim();
        if (cron is not null && !CronExpression.TryParse(cron, CronFormat.Standard, out _))
        {
            return ErrorResponse.From(new Validation("fullBackupCron must be a standard 5-field cron expression"));
        }

        if (request.RetentionDays is not { } retentionDays || retentionDays is < 1 or > MaxRetentionDays)
        {
            return ErrorResponse.From(new Validation("retentionDays must be an integer between 1 and 35"));
        }

        return await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await backups.SetPolicyAsync(
                ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), database, cron, retentionDays, request.Disabled ?? false, cancellationToken) switch
            {
                BackupPolicy policy => TypedResults.Ok(policy),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}
