using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraBase.Features.Replication;

namespace TerraBase.Features.Management.Backups;

/// <summary>PUT a project's customer backup destination.</summary>
public sealed class SetBackupDestinationEndpoint(IManagementService management, IProjectBackupDestinations destinations, ICatalogGate gate)
    : Endpoint<ProjectBackupDestinationRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(BackupDestinationRoute.Path);
        Name("setBackupDestination");
        Summary("Put a project's customer backup destination");
        Description(BackupDestinationRoute.Description);
        Tags("Backups");
        Produces<ProjectBackupDestination>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(ProjectBackupDestinationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (BackupDestinationRoute.IsDatabaseScoped)
        {
            return ErrorResponse.From(new NotFound());
        }

        return await ManagementRoute.CheckWritableProjectAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            Project project => BackupDestinationRoute.Validate(request) is { } error ? ErrorResponse.From(error) : await SaveAsync(project, request, cancellationToken),
        };
    }

    private async ValueTask<IResult> SaveAsync(Project project, ProjectBackupDestinationRequest request, CancellationToken cancellationToken)
    {
        // Replacing a destination revokes the old one: a restore that lost the replacement would resume shipping to the bucket
        // the customer replaced, so it is anchored before it is acknowledged (CatalogGate). A first destination revokes nothing.
        var replacing = await destinations.ReadAsync(ManagementRoute.OrganizationId(HttpContext), project.Id, cancellationToken) is ProjectBackupDestinationRecord;
        switch (await destinations.SaveAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), project.Id, request, cancellationToken))
        {
            case ProjectBackupDestination saved:
                if (replacing)
                {
                    await gate.AnchorAsync(cancellationToken);
                }

                return TypedResults.Ok(saved);
            case NotFound missing:
                return ErrorResponse.From(missing);
            case Forbidden refused:
                return ErrorResponse.From(refused);
            case Unavailable unavailable:
                return ErrorResponse.From(unavailable);
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }
}
