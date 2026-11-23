namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union GetFunctionStatsReply(
    FunctionStats,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class GetFunctionStatsEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionStatsRequest, GetFunctionStatsReply>
{
    private static readonly PlatformInvalidRequest BadWindow = new("window must be 1h, 24h or 7d");

    public override void Configure()
    {
        Get(FunctionNames.RouteItem + "/stats");
        Name("getFunctionStats");
        Summary("Reads a function's invocation metrics");
        Description(FunctionNames.Description + " Query window=1h|24h|7d (default 24h). UTC buckets include empty intervals; percentiles are histogram estimates.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<GetFunctionStatsReply> HandleAsync(
        FunctionStatsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = request.Window ?? "24h";
        if (HttpContext.Request.Query.Keys.Any(static key => key != "window") || window is not ("1h" or "24h" or "7d"))
        {
            return BadWindow;
        }

        var outcome = await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken);
        return outcome switch
        {
            FunctionTarget target => await HandleAuthorizedAsync(target),
            PlatformInvalidRequest invalid => invalid,
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<GetFunctionStatsReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.StatsAsync(target, request.Function, window, cancellationToken);
            return operation switch
            {
                FunctionStats stats => stats,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
