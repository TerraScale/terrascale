using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class GetRulesVersionEndpoint(IManagementService management, IRules rules) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(RulesRoute.Version);
        Name("getDatabaseRulesVersion");
        Summary("Reads one rules version");
        Description("Answers that version's rules; 404 unknown_version when it is not among the last 100. " + RulesRoute.Description);
        Tags("Rules");
        Produces<RulesBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.RouteValues["version"] is not string version || !EntityIds.IsValid(version))
        {
            return ErrorResponse.From(RulesRoute.UnknownVersion);
        }

        var resolved = await RulesRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (resolved is not TenantContext database)
        {
            return (IResult)resolved;
        }

        return await rules.VersionAsync(database, version, cancellationToken) switch
        {
            StoredRules stored => RulesRoute.Body(HttpContext, stored, null, null),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
