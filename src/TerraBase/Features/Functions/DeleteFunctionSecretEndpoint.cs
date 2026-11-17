using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

internal sealed class DeleteFunctionSecretEndpoint(IManagementService management, IFunctions functions) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(FunctionRoute.Secret);
        Name("deleteFunctionSecret");
        Summary("Deletes a function secret");
        Description(FunctionRoute.Description + " The next invocation no longer sees the name in event.secrets.");
        Tags("Functions");
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlRead, ApiKeyScopes.SqlWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var secret = HttpContext.Request.RouteValues["secret"] as string ?? "";
        return FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.DeleteSecretAsync(tenant, name, secret, cancellationToken), cancellationToken);
    }
}
