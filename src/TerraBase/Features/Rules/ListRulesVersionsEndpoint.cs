using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class ListRulesVersionsEndpoint(RulesService rules, RulesStore store)
    : Endpoint<RulesRouteInput, Result<RulesVersionsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(RulesRoute.Versions);
        Name("listDatabaseRulesVersions");
        Summary("Lists a database's rules versions, oldest first");
        Description("Query: after (exclusive version cursor), limit (1 to 100, default 20).");
        Tags("Rules");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<RulesVersionsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        RulesRouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementService.Limit(request.Limit) switch
        {
            int limit => await rules.ResolveAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken) switch
            {
                ResolvedDatabase resolved => await store.VersionsAsync(resolved.Database.Id, request.After, limit, cancellationToken) switch
                {
                    RulesVersionsBody versions => versions,
                    Unavailable unavailable => unavailable,
                },
                Validation invalid => invalid,
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
        };
    }
}
