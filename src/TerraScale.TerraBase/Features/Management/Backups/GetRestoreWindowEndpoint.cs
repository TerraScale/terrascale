using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>GET …/databases/{database}/point-in-time</c>: the times a database can be restored to.</summary>
public sealed class GetRestoreWindowEndpoint(IManagementService management, IPointInTimeRestore restore) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/point-in-time");
        Name("getRestoreWindow");
        Summary("Reads the point-in-time restore window of a database");
        Description("Query destination selects platform (default) or customer. earliest is the oldest continuous backup snapshot of the database limited by its plan's retention window, latest the newest data in the backup (normally about a second old). 404 when continuous backup is off or holds no data of the database within its plan's retention window." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<RestoreWindow>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is not { } database)
        {
            return ErrorResponse.From(new NotFound());
        }

        return BackupDestinationRoute.Source(HttpContext.Request.Query["destination"]) switch
        {
            Validation error => ErrorResponse.From(error),
            string source => await restore.GetWindowAsync(database, cancellationToken, source) is { } window
                ? TypedResults.Ok(window) : ErrorResponse.From(new NotFound()),
        };
    }
}
