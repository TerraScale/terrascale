using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>POST …/databases/{database}/point-in-time/restore</c>: restores a database as of a time into a new database.</summary>
public sealed class RestorePointInTimeEndpoint(IManagementService management, IManagedBackupService backups)
    : Endpoint<PointInTimeRestoreRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Database + "/point-in-time/restore");
        Name("restorePointInTime");
        Summary("Restores a database as of a time into a new database");
        Description("Restores every transaction continuous backup read by pointInTime (backup reads each second) into a new database in the same project, with branchedFrom set to the restored database; the database itself is unchanged. destination selects platform (default) or customer. pointInTime must lie between the restore window's earliest and latest times, inclusive (GET …/point-in-time). name follows the database name rules; without it the first free of <source>-restore, <source>-restore-2, … is used." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabase>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(PointInTimeRestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Access first: another tenant's database answers 404 whatever the body holds.
        return await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            TenantContext database => await RestoreAsync(database, request, cancellationToken),
        };
    }

    private async ValueTask<IResult> RestoreAsync(TenantContext database, PointInTimeRestoreRequest request, CancellationToken cancellationToken)
    {
        var selected = BackupDestinationRoute.Source(request.Destination);
        if (selected is Validation selectionError)
        {
            return ErrorResponse.From(selectionError);
        }

        var destination = string.IsNullOrEmpty(request.Destination) ? "platform" : request.Destination;
        if (request.PointInTime is not { } at)
        {
            return ErrorResponse.From(new Validation("pointInTime is required"));
        }

        string? name = null;
        if (request.Name is not null)
        {
            switch (ManagementRoute.CheckDatabaseName(new NameRequest(request.Name)))
            {
                case Validation error:
                    return ErrorResponse.From(error);
                case string valid:
                    name = valid;
                    break;
            }
        }

        var organization = ManagementRoute.OrganizationId(HttpContext);
        return await backups.RestorePointInTimeAsync(ManagementRoute.UserId(HttpContext), organization, database, at, name, cancellationToken, destination) switch
        {
            ManagedDatabase restored => TypedResults.Created($"{ManagementRoute.Organizations}/{organization}/projects/{restored.ProjectId}/databases/{restored.Id}", restored),
            NotFound error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            QuotaExceeded error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
