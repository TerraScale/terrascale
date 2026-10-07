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
        Description("earliest is the oldest continuous backup snapshot of the database, latest the newest data in the backup (normally about a second old). 404 when continuous backup is off or holds nothing of the database yet." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<RestoreWindow>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) is { } database
            && await restore.GetWindowAsync(database, cancellationToken) is { } window
            ? TypedResults.Ok(window)
            : ErrorResponse.From(new NotFound());
}
