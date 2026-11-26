using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.Http;
using TerraScale.Endpoints;

namespace TerraBase.Features.InternalApi;

internal readonly union InternalAuthorizeResponse(CallerGrant, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class InternalAuthorizeEndpoint(DatabaseAccess access, CatalogStore catalog)
    : Endpoint<AuthorizeRequest, InternalAuthorizeResponse>
{
    private static readonly CredentialRejected MissingCaller = new("a bearer token, API key or peer credential is required", "invalid_key");

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

    public override async ValueTask<InternalAuthorizeResponse> HandleAsync(
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
                InvalidInput invalid => invalid,
                CredentialRejected unauthorized => unauthorized,
                CallerForbidden forbidden => forbidden,
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            }
            : await access.ProjectAsync(caller, request.Project, scope, cancellationToken) switch
            {
                ResolvedProject project => await GrantAsync(caller, project.Project, null, request.Scopes, cancellationToken),
                InvalidInput invalid => invalid,
                CredentialRejected unauthorized => unauthorized,
                CallerForbidden forbidden => forbidden,
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            };
    }

    private async ValueTask<InternalAuthorizeResponse> GrantAsync(
        Caller caller, ProjectRecord project, ResolvedDatabase? target, IReadOnlyList<string> requested, CancellationToken cancellationToken)
    {
        List<string> granted = [];
        foreach (var scope in requested.Distinct(StringComparer.Ordinal))
        {
            var allowedOutcome = await access.AuthorizeProjectAsync(caller, project, target?.Database, scope, cancellationToken);
            var allowed = allowedOutcome switch
            {
                Completed => scope != Scopes.SqlWrite || target is not { CanWrite: false },
                CallerForbidden or ResourceNotFound => false,
                BackendUnavailable => false,
            };
            if (allowed)
            {
                granted.Add(scope);
            }
        }

        var outcome = await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken);
        return outcome switch
        {
            OrganizationRecord organization => new CallerGrant(
                organization.Id,
                project.Id,
                target?.Database.Id,
                target?.Database.Name,
                caller is EndUserCaller,
                Anonymous: caller is EndUserCaller { Auth.IsAnonymous: true },
                Restricted(caller),
                CallerContext.Actor(caller),
                Roles(caller),
                granted),
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
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
