using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>
/// <c>GET /admin/projects/{project}/databases</c>: the local page of a project's databases, for a peer region's read
/// fan-out. Internal listener only, behind the shared peer credential. A project this node does not hold answers an
/// empty page: the asking region merges the pages of every peer.
/// </summary>
internal sealed class AdminListProjectDatabasesEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
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

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var project = ManagementRoute.Value(HttpContext, "project");
        if (!EntityIds.IsValid(project))
        {
            return ValueTask.FromResult(ErrorResponse.From(new Validation("project must be a canonical uppercase ULID")));
        }

        return ValueTask.FromResult(ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => PageAsync(project, page, cancellationToken).AsTask(),
            _ => throw new UnreachableException(),
        });
    }

    private async ValueTask<IResult> PageAsync(string project, PageQuery page, CancellationToken cancellationToken) =>
        await management.ListProjectDatabasesAsync(project, page.After, MaxDatabases, cancellationToken).ConfigureAwait(false) switch
        {
            ManagementPage<ManagedDatabase> local => TypedResults.Ok(new ManagementPage<ManagedDatabaseBody>(
                [.. local.Items.Select(item => ManagedDatabaseBody.From(item, Listeners.RegionOf(HttpContext)))], local.Next)),
            Unavailable unavailable => ErrorResponse.From(unavailable),
            _ => throw new InvalidOperationException(),
        };

    private const int MaxDatabases = 100;
}
