using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class AdminListProjectDatabasesEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/admin/projects/{project}/databases");
        Name("adminListProjectDatabases");
        Summary("Lists a project's local databases for a peer region");
        Description("Internal peer listener only, behind the shared peer credential; 404 elsewhere. Version 1. Answers up to 100 "
            + "databases in id order with this node's region and a next cursor when more remain. A project this node does "
            + "not hold answers an empty page: the asking region merges every peer's page.");
        Tags("Administration");
        Produces<ManagementPage<ManagedDatabaseBody>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var project = HttpContext.RouteValue("project");
        if (!EntityIds.IsValid(project))
        {
            return ErrorResponse.From(new Validation("project must be a canonical uppercase ULID"));
        }

        return ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await PageAsync(project, page, cancellationToken).ConfigureAwait(false),
        };
    }

    private async ValueTask<IResult> PageAsync(string project, PageQuery page, CancellationToken cancellationToken) =>
        await management.ListProjectDatabasesAsync(project, page.After, MaxDatabases, cancellationToken).ConfigureAwait(false) switch
        {
            ManagementPage<ManagedDatabase> local => TypedResults.Ok(new ManagementPage<ManagedDatabaseBody>(
                [.. local.Items.Select(item => ManagedDatabaseBody.From(item, Listeners.RegionOf(HttpContext)))], local.Next)),
            Unavailable unavailable => ErrorResponse.From(unavailable),
        };

    private const int MaxDatabases = 100;
}
