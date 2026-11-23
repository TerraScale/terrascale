using System.Globalization;

namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class ListFunctionLogsEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionLogsRequest, Result<FunctionLogList, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    private static readonly Validation BadLimit = new("limit must be an integer from 1 to 100");

    public override void Configure()
    {
        Get(FunctionNames.RouteItem + "/logs");
        Name("listFunctionLogs");
        Summary("Lists a function's invocation logs");
        Description(FunctionNames.Description + " Newest first; query limit=1..100 (default 50). The last 100 invocations are kept.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FunctionLogList, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        FunctionLogsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = 50;
        if (HttpContext.Request.Query.Keys.Any(static key => key != "limit")
            || request.Limit is { } raw && (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
        {
            return BadLimit;
        }

        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken) switch
        {
            FunctionTarget target => await functions.LogsAsync(target, request.Function, limit, cancellationToken) switch
            {
                FunctionLogList logs => logs,
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
