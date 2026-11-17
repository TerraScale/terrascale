using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class PutRulesEndpoint(IManagementService management, IRules rules, TerraBaseOptions options) : Endpoint<RulesSet, IResult>
{
    public override void Configure()
    {
        Put(RulesRoute.Rules);
        Metadata(AnchoredRevocation.Instance);
        Name("putDatabaseRules");
        Summary("Deploys a database's rules");
        Description("Replaces the whole rules set and answers the new version with its warnings. To deny every end-user request, deploy {\"tables\":{}}. "
            + "The last 100 versions are kept." + RulesRoute.WriteDescription + RulesRoute.Description);
        Tags("Rules");
        Produces<RulesBody>();
        Produces<RulesErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status428PreconditionRequired);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    public override async ValueTask<IResult> HandleAsync(RulesSet request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tables is null || request.Tables.Values.Any(table => table is null))
        {
            return ErrorResponse.From(new Validation("tables must be an object of table rules"));
        }

        if (RulesRoute.Precondition(HttpContext.Request) is not { } precondition)
        {
            return RulesRoute.Required();
        }

        if (await RulesRoute.ElsewhereAsync(HttpContext, management, options) is { } elsewhere)
        {
            return elsewhere;
        }

        var resolved = await RulesRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        return resolved is TenantContext database
            ? RulesRoute.Deployed(HttpContext, await rules.DeployAsync(database, new RulesDeploy(request), precondition, ManagementRoute.UserId(HttpContext), cancellationToken))
            : (IResult)resolved;
    }
}
