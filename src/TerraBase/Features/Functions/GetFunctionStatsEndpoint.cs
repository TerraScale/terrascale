using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Contracts.Functions;

namespace TerraBase.Features.Functions;

internal sealed class GetFunctionStatsEndpoint(IManagementService management, IFunctions functions) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(FunctionRoute.Item + "/stats");
        Name("getFunctionStats");
        Summary("Reads a function's invocation metrics");
        Description(FunctionRoute.Description + " Query window=1h|24h|7d (default 24h). UTC buckets include empty intervals; percentiles are histogram estimates.");
        Tags("Functions");
        Produces<TerraBaseFunctionStats>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.SqlRead, ApiKeyScopes.SqlWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var query = HttpContext.Request.Query;
        var window = query.TryGetValue("window", out var raw) ? raw.ToString() : "24h";
        return query.Keys.Any(key => key != "window") || window is not ("1h" or "24h" or "7d")
            ? ValueTask.FromResult(ErrorResponse.From(new Validation("window must be 1h, 24h or 7d")))
            : FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.StatsAsync(tenant, name, window, cancellationToken), cancellationToken);
    }
}
