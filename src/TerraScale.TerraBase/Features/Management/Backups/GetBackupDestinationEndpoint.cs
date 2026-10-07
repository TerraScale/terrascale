using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>GET a project's customer backup destination.</summary>
public sealed class GetBackupDestinationEndpoint(IManagementService management, IProjectBackupDestinations destinations) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(BackupDestinationRoute.Path);
        Name("getBackupDestination");
        Summary("Get a project's customer backup destination");
        Description(BackupDestinationRoute.Description);
        Tags(ManagementRoute.Tag);
        Produces<ProjectBackupDestination>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) is { } project
            && await destinations.ReadAsync(ManagementRoute.OrganizationId(HttpContext), project.Id, cancellationToken) is { } record
                ? TypedResults.Ok(record.Destination)
                : ErrorResponse.From(new NotFound());
}
