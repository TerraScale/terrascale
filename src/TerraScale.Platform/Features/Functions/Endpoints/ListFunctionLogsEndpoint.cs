using System.Globalization;

namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union ListFunctionLogsReply(
    FunctionLogList,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class ListFunctionLogsEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionLogsRequest, ListFunctionLogsReply>
{
    private static readonly PlatformInvalidRequest BadLimit = new("limit must be an integer from 1 to 100");

    public override void Configure()
    {
        Get(FunctionNames.RouteItem + "/logs");
        Name("listFunctionLogs");
        Summary("Lists a function's invocation logs");
        Description(FunctionNames.Description + " Newest first; query limit=1..100 (default 50). The last 100 invocations are kept.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<ListFunctionLogsReply> HandleAsync(
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

        async ValueTask<ListFunctionLogsReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.LogsAsync(target, request.Function, limit, cancellationToken);
            return operation switch
            {
                FunctionLogList logs => logs,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}
