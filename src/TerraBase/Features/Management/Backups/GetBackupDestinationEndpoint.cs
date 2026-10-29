using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>GET a project's customer backup destination.</summary>
internal sealed class GetBackupDestinationEndpoint(IManagementService management, IProjectBackupDestinations destinations) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(BackupDestinationRoute.Path);
        Name("getBackupDestination");
        Summary("Get a project's customer backup destination");
        Description(BackupDestinationRoute.Description);
        Tags("Backups");
        Produces<ProjectBackupDestination>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        BackupDestinationRoute.IsDatabaseScoped ? ErrorResponse.From(new NotFound())
            : await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) switch
            {
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                Project project => await destinations.ReadAsync(ManagementRoute.OrganizationId(HttpContext), project.Id, cancellationToken) switch
                {
                    ProjectBackupDestinationRecord record => TypedResults.Ok(record.Destination),
                    NotFound error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                },
            };
}
