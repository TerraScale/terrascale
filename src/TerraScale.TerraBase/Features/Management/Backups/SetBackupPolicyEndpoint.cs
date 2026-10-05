using Cronos;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Backups;

/// <summary><c>PUT …/databases/{database}/backup-policy</c>: saves the backup policy of a database.</summary>
public sealed class SetBackupPolicyEndpoint(IManagementService management, IManagedBackupService backups) : Endpoint<BackupPolicyRequest, IResult>
{
    private const int MaxRetentionDays = 35;

    /// <inheritdoc />
    public override void Configure()
    {
        Put(ManagementRoute.Database + "/backup-policy");
        Name("setBackupPolicy");
        Summary("Saves the backup policy of a database");
        Description("fullBackupCron is a standard 5-field cron expression evaluated in UTC, or null or empty for no scheduled backups. retentionDays (1 to 35) applies to manual and scheduled backups. disabled pauses scheduled backups." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<BackupPolicy>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(BackupPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cron = string.IsNullOrWhiteSpace(request.FullBackupCron) ? null : request.FullBackupCron.Trim();
        if (cron is not null && !CronExpression.TryParse(cron, CronFormat.Standard, out _))
        {
            throw new BadHttpRequestException("fullBackupCron must be a standard 5-field cron expression");
        }

        if (request.RetentionDays is not (>= 1 and <= MaxRetentionDays))
        {
            throw new BadHttpRequestException("retentionDays must be an integer between 1 and 35");
        }

        if (await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is not { } database)
        {
            return TypedResults.NotFound();
        }

        return ManagementRoute.Ok(await backups.SetPolicyAsync(
            ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), database, cron, request.RetentionDays.Value, request.Disabled ?? false, cancellationToken));
    }
}
