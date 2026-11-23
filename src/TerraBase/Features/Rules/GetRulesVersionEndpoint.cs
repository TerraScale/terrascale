using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class GetRulesVersionEndpoint(RulesService rules, RulesStore store)
    : Endpoint<RulesRouteInput, RulesReadResponse>
{
    public override void Configure()
    {
        Get(RulesRoute.Version);
        Name("getDatabaseRulesVersion");
        Summary("Reads one rules version");
        Tags("Rules");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<RulesReadResponse> HandleAsync(
        RulesRouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await rules.ResolveAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await store.VersionAsync(resolved.Database.Id, request.Version, cancellationToken) switch
            {
                StoredRules version => new RulesResponse(version, [], RulesService.Warnings(version.Rules)),
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
