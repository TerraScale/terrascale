using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

internal sealed class SetFunctionSecretEndpoint(IManagementService management, IFunctions functions) : Endpoint<SetFunctionSecretRequest, IResult>
{
    public override void Configure()
    {
        Put(FunctionRoute.Secret);
        Name("setFunctionSecret");
        Summary("Sets or replaces a function secret");
        Description(FunctionRoute.Description + " Body {value}: at most 4 KiB of UTF-8, sealed at rest under the master keyring and readable only by the handler "
            + "as event.secrets from the next invocation. Names match ^[A-Z_][A-Z0-9_]{0,63}$; at most 50 secrets per function. The response never contains the value.");
        Tags("Functions");
        Metadata(new RequestBodyMetadata(typeof(SetFunctionSecretRequest), "application/json"));
        Produces<FunctionSecretBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlRead, ApiKeyScopes.SqlWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    public override ValueTask<IResult> HandleAsync(SetFunctionSecretRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var secret = HttpContext.Request.RouteValues["secret"] as string ?? "";
        return FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.SetSecretAsync(tenant, name, secret, request, cancellationToken), cancellationToken);
    }
}
