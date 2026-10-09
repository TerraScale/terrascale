using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>DELETE a project's customer backup destination.</summary>
public sealed class DeleteBackupDestinationEndpoint(IProjectBackupDestinations destinations) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(BackupDestinationRoute.Path);
        Name("deleteBackupDestination");
        Summary("Delete a project's customer backup destination");
        Description(BackupDestinationRoute.Description);
        Tags("Backups");
        Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        BackupDestinationRoute.IsDatabaseScoped ? ErrorResponse.From(new NotFound())
        : await destinations.DeleteAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
        };
}
