using System.Globalization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

internal static class FunctionRoute
{
    public const string Collection = ProjectApiRoute.Database + "/functions";
    public const string Item = Collection + "/{function}";
    public const string Description = "Project-token database functions (docs/functions/design.md). Deploy, logs and delete require documents:read and "
        + "documents:write; invoke requires documents:read. A function is JavaScript defining handler(event); it runs with the function's authority. "
        + ProjectApiRoute.IdentityDescription;

    public static async ValueTask<IResult> RunAsync(HttpContext context, IManagementService management,
        Func<TenantContext, string, ValueTask<FunctionResult>> operation, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var raw = context.Request.RouteValues["function"] as string;
        var name = "";
        if (raw is not null)
        {
            if (!DatabaseName.TryParse(raw, out var parsed))
            {
                return ErrorResponse.From(new Validation("function must be a lowercase name of at most 63 characters"));
            }

            name = parsed.Value;
        }

        return await ManagementRoute.DatabaseAsync(context, management, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext database => Result(await operation(database, name).ConfigureAwait(false)),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    public static Valid<int> Limit(HttpContext context)
    {
        var query = context.Request.Query;
        var limit = 50;
        if (query.Keys.Any(key => key is not "limit")
            || query.TryGetValue("limit", out var raw) && (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
        {
            return new Validation("limit must be an integer from 1 to 100");
        }

        return limit;
    }

    private static IResult Result(FunctionResult result) => result switch
    {
        FunctionBody function => TypedResults.Ok(function),
        FunctionBody[] functions => TypedResults.Ok(functions),
        FunctionLogBody[] logs => TypedResults.Ok(logs),
        FunctionResponse response => new Answer(response),
        Done => TypedResults.NoContent(),
        Validation error => ErrorResponse.From(error),
        NotFound error => ErrorResponse.From(error),
        Conflict error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };

    private sealed class Answer(FunctionResponse response) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = response.Status;
            httpContext.Response.ContentType = response.ContentType;
            httpContext.Response.ContentLength = response.Body.Length;
            await httpContext.Response.Body.WriteAsync(response.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
