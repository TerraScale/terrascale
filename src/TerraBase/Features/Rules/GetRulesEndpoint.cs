using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class GetRulesEndpoint(IManagementService management, IRules rules) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(RulesRoute.Rules);
        Name("getDatabaseRules");
        Summary("Reads a database's rules");
        Description("Answers the deployed rules with ETag: \"<version>\". problems lists every rule that no longer compiles against the current schema; "
            + "end-user requests that need such a rule answer 403 rules_invalid until the rules are deployed again. 404 no_rules when none were deployed. "
            + RulesRoute.Description);
        Tags("Rules");
        Produces<RulesBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var resolved = await RulesRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (resolved is not TenantContext database)
        {
            return (IResult)resolved;
        }

        return await rules.GetAsync(database, cancellationToken) switch
        {
            CurrentRules current => RulesRoute.Body(HttpContext, current.Rules, current.Problems, null),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
