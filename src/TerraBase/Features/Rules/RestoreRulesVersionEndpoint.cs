using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class RestoreRulesVersionEndpoint(RulesService rules, RulesStore store) : Endpoint<RulesRouteInput, RulesWriteResponse>
{
    public override void Configure()
    {
        Post(RulesRoute.Restore);
        Name("restoreDatabaseRules");
        Summary("Deploys an earlier rules version again");
        Description("Same preconditions as PUT rules; the restored rules are compiled against the current schema first.");
        Tags("Rules");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<RulesWriteResponse> HandleAsync(RulesRouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await rules.ResolveAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesWrite, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await store.VersionAsync(resolved.Database.Id, request.Version, cancellationToken) switch
            {
                StoredRules version => RulesWrites.Map(await rules.DeployAsync(HttpContext.Caller, request.Project, request.Database, version.Rules,
                    RulesRoute.IfMatch(HttpContext.Request), RulesRoute.IfNoneMatchAny(HttpContext.Request), cancellationToken)),
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
