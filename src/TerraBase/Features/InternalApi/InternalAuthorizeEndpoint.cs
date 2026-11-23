using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.Http;
using TerraScale.Endpoints;

namespace TerraBase.Features.InternalApi;

internal sealed class InternalAuthorizeEndpoint(DatabaseAccess access, CatalogStore catalog)
    : Endpoint<AuthorizeRequest, Result<CallerGrant, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    private static readonly Unauthorized MissingCaller = new("a bearer token, API key or peer credential is required", "invalid_key");

    public override void Configure()
    {
        Post(InternalRoute.Root + "/authorize");
        Name("authorizeInternalCaller");
        Summary("Resolves a forwarded caller credential into a grant on a project or database");
        Description("Platform services forward the caller's Authorization header (or X-TerraScale-Caller-Peer: 1 for the platform peer itself) "
            + "and receive the organization, project, database and scopes the caller holds.");
        Tags(InternalRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<CallerGrant, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        AuthorizeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = HttpContext.Caller;
        if (caller is AnonymousCaller || (caller is PeerCaller && !HttpContext.Request.Headers.ContainsKey(PlatformHeaders.CallerPeer)))
        {
            return MissingCaller;
        }

        var scope = request.Scopes is [var first, ..] ? first : Scopes.SqlRead;
        return request.Database is { } database
            ? await access.DataAsync(caller, request.Project, database, cancellationToken) switch
            {
                ResolvedDatabase target => await GrantAsync(caller, target.Project, target, request.Scopes, cancellationToken),
                Validation invalid => invalid,
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            }
            : await access.ProjectAsync(caller, request.Project, scope, cancellationToken) switch
            {
                ResolvedProject project => await GrantAsync(caller, project.Project, null, request.Scopes, cancellationToken),
                Validation invalid => invalid,
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            };
    }

    private async ValueTask<Result<CallerGrant, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> GrantAsync(
        Caller caller, ProjectRecord project, ResolvedDatabase? target, IReadOnlyList<string> requested, CancellationToken cancellationToken)
    {
        List<string> granted = [];
        foreach (var scope in requested.Distinct(StringComparer.Ordinal))
        {
            var allowed = await access.AuthorizeProjectAsync(caller, project, target?.Database, scope, cancellationToken) switch
            {
                Done => scope != Scopes.SqlWrite || target is not { CanWrite: false },
                Forbidden or NotFound => false,
                Unavailable => false,
            };
            if (allowed)
            {
                granted.Add(scope);
            }
        }

        return await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken) switch
        {
            OrganizationRecord organization => new CallerGrant(
                organization.Id,
                project.Id,
                target?.Database.Id,
                target?.Database.Name,
                caller is EndUserCaller,
                Anonymous: false,
                Restricted(caller),
                CallerContext.Actor(caller),
                Roles(caller),
                granted),
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private static bool Restricted(Caller caller) => caller switch
    {
        ApiKeyCaller { Key.DatabaseId: not null } or ProjectCaller { Database: not null } or EndUserCaller { Database: not null } => true,
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => false,
    };

    private static IReadOnlyList<string> Roles(Caller caller) => caller switch
    {
        EndUserCaller { Auth.OrgRole: { } role } => [role],
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => [],
    };
}
