namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class GetFunctionStatsEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionStatsRequest, Result<FunctionStats, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    private static readonly Validation BadWindow = new("window must be 1h, 24h or 7d");

    public override void Configure()
    {
        Get(FunctionNames.RouteItem + "/stats");
        Name("getFunctionStats");
        Summary("Reads a function's invocation metrics");
        Description(FunctionNames.Description + " Query window=1h|24h|7d (default 24h). UTC buckets include empty intervals; percentiles are histogram estimates.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FunctionStats, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        FunctionStatsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = request.Window ?? "24h";
        if (HttpContext.Request.Query.Keys.Any(static key => key != "window") || window is not ("1h" or "24h" or "7d"))
        {
            return BadWindow;
        }

        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken) switch
        {
            FunctionTarget target => await functions.StatsAsync(target, request.Function, window, cancellationToken) switch
            {
                FunctionStats stats => stats,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
