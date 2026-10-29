using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

/// <summary>Deletes a function and its logs.</summary>
internal sealed class DeleteFunctionEndpoint(IManagementService management, IFunctions functions) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(FunctionRoute.Item);
        Name("deleteFunction");
        Summary("Deletes a function and its logs");
        Description(FunctionRoute.Description + "");
        Tags("Functions");
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.DeleteAsync(tenant, name, cancellationToken), cancellationToken);
}
