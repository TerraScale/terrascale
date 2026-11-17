using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class RestoreRulesVersionEndpoint(IManagementService management, IRules rules, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(RulesRoute.Restore);
        Metadata(AnchoredRevocation.Instance);
        Name("restoreDatabaseRulesVersion");
        Summary("Restores an earlier rules version");
        Description("Deploys the body of an earlier version as a new version, checked against today's schema like any deploy. 404 unknown_version when the "
            + "version is not among the kept ones." + RulesRoute.WriteDescription + RulesRoute.Description);
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

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.RouteValues["version"] is not string version || !EntityIds.IsValid(version))
        {
            return ErrorResponse.From(RulesRoute.UnknownVersion);
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
            ? RulesRoute.Deployed(HttpContext, await rules.DeployAsync(database, new RulesRestore(version), precondition, ManagementRoute.UserId(HttpContext), cancellationToken))
            : (IResult)resolved;
    }
}
