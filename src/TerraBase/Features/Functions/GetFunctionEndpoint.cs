using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

/// <summary>Reads a function with its source.</summary>
internal sealed class GetFunctionEndpoint(IManagementService management, IFunctions functions) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(FunctionRoute.Item);
        Name("getFunction");
        Summary("Reads a function with its source");
        Description(FunctionRoute.Description + "");
        Tags("Functions");
        Produces<FunctionBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.GetAsync(tenant, name, cancellationToken), cancellationToken);
}
