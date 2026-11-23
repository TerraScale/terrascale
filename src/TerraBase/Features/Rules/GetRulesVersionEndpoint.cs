using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class GetRulesVersionEndpoint(RulesService rules, RulesStore store)
    : Endpoint<RulesRouteInput, Result<RulesResponse, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<RulesResponse, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        RulesRouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await rules.ResolveAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedDatabase resolved => await store.VersionAsync(resolved.Database.Id, request.Version, cancellationToken) switch
            {
                StoredRules version => new RulesResponse(version, [], RulesService.Warnings(version.Rules)),
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
