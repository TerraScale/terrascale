using System.Collections.Frozen;
using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Regions;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class ListManagedDatabasesEndpoint(
    IManagementService management, IProjectCatalog catalog, IRegionPeers regions, RemoteDatabases remote) : EndpointWithoutRequest<IResult>
{
    internal const string ListOperationName = "listDatabases";

    public override void Configure()
    {
        Get(ProjectApiRoute.Databases);
        Name(ListOperationName);
        Summary("Lists the databases of a project");
        Description(ManagementRoute.PageDescription + " " + ProjectApiRoute.IdentityDescription
            + " Every serving region's page is merged, so a database appears whichever region homes it; items are in id order."
            + " A database whose home region no longer lists it — one its home deleted — is not listed, whatever a replica region still records.");
        Tags("Databases");
        Produces<ManagementPage<ManagedDatabaseBody>>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

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
        if (HttpContext.Request.RouteValues.TryGetValue("organization", out var bound) && bound is string known)
        {
            return await ListAsync(ManagementRoute.UserId(HttpContext), known, project, null, null, page, cancellationToken).ConfigureAwait(false);
        }

        var actor = ManagementRoute.UserId(HttpContext);
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

        var restricted = HttpContext.User.FindFirst("database")?.Value;
        string? restrictedId = null;
        if (restricted is not null)
        {
            switch (await management.FindDatabaseRecordAsync(owner, project, restricted, cancellationToken).ConfigureAwait(false))
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

        HttpContext.Request.RouteValues["organization"] = owner;
        IResult result = null!;
        var grant = new ProjectAccess(actor, project, owner, restricted, restrictedId)
        {
            IsEndUser = HttpContext.User.HasClaim("token_kind", "end_user"),
            Roles = HttpContext.User.FindAll("roles").Select(claim => claim.Value).ToFrozenSet(StringComparer.Ordinal),
        };
        await ProjectAccess.RunAsync(grant, async () => result = await ListAsync(grant.Actor, owner, project, restricted, grant, page, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        return result;
    }

    private async ValueTask<IResult> ListAsync(
        string actor, string organization, string project, string? restricted, ProjectAccess? grant, PageQuery page, CancellationToken cancellationToken) =>
        await management.ListDatabasesAsync(actor, organization, project, page.After, page.Limit, cancellationToken).ConfigureAwait(false) switch
        {
            ManagementPage<ManagedDatabase> local => TypedResults.Ok(await MergedAsync(local, restricted is not null, page, project, cancellationToken).ConfigureAwait(false)),
            NotFound error => grant is null ? ErrorResponse.From(error) : ErrorResponse.From(Unknown()),
            Unavailable unavailable => ErrorResponse.From(unavailable),
            _ => throw new InvalidOperationException(),
        };

    private async ValueTask<ManagementPage<ManagedDatabaseBody>> MergedAsync(
        ManagementPage<ManagedDatabase> local, bool restricted, PageQuery page, string project, CancellationToken cancellationToken)
    {
        var region = Listeners.RegionOf(HttpContext);
        if (restricted)
        {
            return new([.. local.Items.Select(database => ManagedDatabaseBody.From(database, region))], local.Next);
        }

        var items = local.Items.Select(database => ManagedDatabaseBody.From(database, region)).ToList();
        var more = local.Next is not null;
        var peers = await regions.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
        var windows = new Dictionary<string, (HashSet<string> Ids, string? Last)>(StringComparer.Ordinal)
        {
            [region] = ([.. local.Items.Select(database => database.Id)], local.Next is null ? null : local.Items[^1].Id),
        };
        if (peers.Count > 0)
        {
            var pages = await Task.WhenAll(peers.Select(peer => remote.PageAsync(peer.Address, project, page.After, page.Limit, cancellationToken)))
                .ConfigureAwait(false);
            for (var index = 0; index < pages.Length; index++)
            {
                var fetched = pages[index];
                var listed = fetched?.Items.Where(database => database.ProjectId == project).ToList();
                if (listed is null || listed.Count == 0 && fetched!.Next is not null)
                {
                    continue;
                }

                windows[peers[index].Region] = (
                    [.. listed.Select(database => database.Id)],
                    fetched!.Next is null ? null : listed[^1].Id);
                items.AddRange(listed);
                more |= fetched.Next is not null;
            }
        }

        var ordered = items.DistinctBy(database => database.Id)
            .OrderBy(database => database.Id, StringComparer.Ordinal)
            .SkipWhile(database => page.After is not null && string.CompareOrdinal(database.Id, page.After) <= 0)
            .Take(page.Limit + 1)
            .ToList();
        bool Ghost(ManagedDatabaseBody candidate) =>
            windows.TryGetValue(candidate.HomeRegion, out var window)
            && (window.Last is null || string.CompareOrdinal(candidate.Id, window.Last) <= 0) && !window.Ids.Contains(candidate.Id);
        var kept = ordered.Where(database => !Ghost(database)).ToList();
        var truncated = kept.Count > page.Limit;
        var shown = truncated ? [.. kept.Take(page.Limit)] : kept;
        return new(shown, shown.Count > 0 && (truncated || more) ? shown[^1].Id : null);
    }

    private static NotFound Unknown() => new("the database does not exist", "unknown_database");
}
