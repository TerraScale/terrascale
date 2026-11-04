using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Regions;

internal sealed class LocateHomeEndpoint(IProjectCatalog catalog, IManagementService management, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(HomeLocator.Path);
        Name("locateDatabaseHome");
        Summary("Answers whether this node is the home of a project's database");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. project names a project and database, when present, one of its databases by name or id. "
            + "200 when this node's catalog holds the project and, when named, the database homed in this region; 404 otherwise.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status200OK);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.Query["project"] is not [{ Length: > 0 } project])
        {
            return ErrorResponse.From(new NotFound());
        }

        if (await catalog.ProjectOrganizationAsync(project, cancellationToken).ConfigureAwait(false) is not string organization)
        {
            return ErrorResponse.From(new NotFound());
        }

        if (HttpContext.Request.Query["database"] is not [{ Length: > 0 } database])
        {
            return Results.Ok();
        }

        return await management.FindDatabaseRecordAsync(organization, project, database, cancellationToken).ConfigureAwait(false) is ManagedDatabase record
            && record.PlacedIn(options.Region)
            ? Results.Ok()
            : ErrorResponse.From(new NotFound());
    }
}
