using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

/// <summary>Lists a function's invocation logs.</summary>
internal sealed class ListFunctionLogsEndpoint(IManagementService management, IFunctions functions) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(FunctionRoute.Item + "/logs");
        Name("listFunctionLogs");
        Summary("Lists a function's invocation logs");
        Description(FunctionRoute.Description + " Newest first; query limit=1..100 (default 50). The last 100 invocations are kept.");
        Tags("Functions");
        Produces<FunctionLogBody[]>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) => FunctionRoute.Limit(HttpContext) switch
    {
        Validation error => ValueTask.FromResult(ErrorResponse.From(error)),
        int limit => FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.LogsAsync(tenant, name, limit, cancellationToken), cancellationToken),
    };
}
