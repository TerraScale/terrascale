using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary>PUT a project's customer backup destination.</summary>
public sealed class SetBackupDestinationEndpoint(IManagementService management, IProjectBackupDestinations destinations) : Endpoint<ProjectBackupDestinationRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(BackupDestinationRoute.Path);
        Name("setBackupDestination");
        Summary("Put a project's customer backup destination");
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
    public override async ValueTask<IResult> HandleAsync(ProjectBackupDestinationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ManagementRoute.CheckWritableProjectAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Project project => BackupDestinationRoute.Validate(request) is { } error ? ErrorResponse.From(error)
                : await destinations.SaveAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), project.Id, request, cancellationToken) switch
                {
                    ProjectBackupDestination saved => TypedResults.Ok(saved),
                    NotFound missing => ErrorResponse.From(missing),
                    Forbidden refused => ErrorResponse.From(refused),
                    Unavailable unavailable => ErrorResponse.From(unavailable),
                },
        };
    }
}
