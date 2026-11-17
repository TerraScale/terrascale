using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

internal sealed class ListFunctionSecretsEndpoint(IManagementService management, IFunctions functions) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(FunctionRoute.Secrets);
        Name("listFunctionSecrets");
        Summary("Lists a function's secret names");
        Description(FunctionRoute.Description + " Direct array in name order with name and updatedAt; values are write-only and never returned.");
        Tags("Functions");
        Produces<FunctionSecretBody[]>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlRead, ApiKeyScopes.SqlWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.ListSecretsAsync(tenant, name, cancellationToken), cancellationToken);
}
