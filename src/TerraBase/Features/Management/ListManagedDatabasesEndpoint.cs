using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET …/projects/{project}/databases</c>: lists the databases of a project.</summary>
internal sealed class ListManagedDatabasesEndpoint(
    IManagementService management, IProjectCatalog catalog, IRemoteRegions regions, RemoteDatabases remote) : EndpointWithoutRequest<IResult>
{
    internal const string ListOperationName = "listDatabases";

    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Databases);
        Name(ListOperationName);
        Summary("Lists the databases of a project");
        Description(ManagementRoute.PageDescription + " " + ProjectApiRoute.IdentityDescription
            + " Every serving region's page is merged, so a database appears whichever region homes it; items are in id order.");
        Tags("Databases");
        Produces<ManagementPage<ManagedDatabaseBody>>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await ListAsync(page, cancellationToken).ConfigureAwait(false),
            _ => throw new UnreachableException(),
        };

    private async ValueTask<IResult> ListAsync(PageQuery page, CancellationToken cancellationToken)
    {
        var project = ManagementRoute.Value(HttpContext, "project");
        var actor = ManagementRoute.UserId(HttpContext);
        var organization = HttpContext.Request.RouteValues.TryGetValue("organization", out var bound) && bound is string known ? known : null;
        if (organization is null)
        {
            var resolved = await catalog.ResolveProjectAsync(project, actor, createIn: null, cancellationToken).ConfigureAwait(false);
            if (resolved is not string owner)
            {
                return ErrorResponse.From(resolved switch
                {
                    Unavailable unavailable => unavailable,
                    NotFound => Unknown(),
                    string => throw new UnreachableException(),
                });
            }

            organization = owner;
        }

        var restricted = HttpContext.User.FindFirst("database")?.Value;
        string? restrictedId = null;
        if (restricted is not null)
        {
            switch (await management.FindDatabaseRecordAsync(organization, project, restricted, cancellationToken).ConfigureAwait(false))
            {
                case ManagedDatabase record:
                    restrictedId = record.Id;
                    break;
                case Unavailable unavailable:
                    return ErrorResponse.From(unavailable);
                case NotFound:
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        var grant = new ProjectAccess(actor, project, organization, restricted, restrictedId);
        HttpContext.Request.RouteValues["organization"] = organization;
        IResult result = null!;
        await ProjectAccess.RunAsync(grant, async () => result = await ListAsync(grant, organization, project, page, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        return result;
    }

    private async ValueTask<IResult> ListAsync(ProjectAccess grant, string organization, string project, PageQuery page, CancellationToken cancellationToken) =>
        await management.ListDatabasesAsync(grant.Actor, organization, project, page.After, page.Limit, cancellationToken).ConfigureAwait(false) switch
        {
            ManagementPage<ManagedDatabase> local => TypedResults.Ok(await MergedAsync(local, grant, page, project, cancellationToken).ConfigureAwait(false)),
            NotFound => ErrorResponse.From(Unknown()),
            Unavailable unavailable => ErrorResponse.From(unavailable),
            _ => throw new InvalidOperationException(),
        };

    /// <summary>
    /// The local page, or when the token is not narrowed to one database, the cluster's: this region's items and each peer
    /// region's, deduplicated by id in id order. A narrowed token keeps its single-database local page.
    /// </summary>
    private async ValueTask<ManagementPage<ManagedDatabaseBody>> MergedAsync(
        ManagementPage<ManagedDatabase> local, ProjectAccess grant, PageQuery page, string project, CancellationToken cancellationToken)
    {
        var region = Listeners.RegionOf(HttpContext);
        if (grant.DatabaseName is not null)
        {
            return new([.. local.Items.Select(database => ManagedDatabaseBody.From(database, region))], local.Next);
        }

        var items = local.Items.Select(database => ManagedDatabaseBody.From(database, region)).ToList();
        var more = local.Next is not null;
        var peers = await regions.InternalListenersAsync(cancellationToken).ConfigureAwait(false);
        if (peers.Count > 0)
        {
            var pages = await Task.WhenAll(peers.Select(peer => remote.PageAsync(peer, project, page.After, page.Limit, cancellationToken)))
                .ConfigureAwait(false);
            foreach (var fetched in pages)
            {
                if (fetched is not null)
                {
                    items.AddRange(fetched.Items.Where(database => database.ProjectId == project));
                    more |= fetched.Next is not null;
                }
            }
        }

        var ordered = items.DistinctBy(database => database.Id)
            .OrderBy(database => database.Id, StringComparer.Ordinal)
            .SkipWhile(database => page.After is not null && string.CompareOrdinal(database.Id, page.After) <= 0)
            .Take(page.Limit + 1)
            .ToList();
        var truncated = ordered.Count > page.Limit;
        var shown = truncated ? [.. ordered.Take(page.Limit)] : ordered;
        return new(shown, shown.Count > 0 && (truncated || more) ? shown[^1].Id : null);
    }

    private static NotFound Unknown() => new("the database does not exist", "unknown_database");
}
