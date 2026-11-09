using System.Diagnostics;
using TerraBase.Hosting;
using System.Globalization;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;
using RefetchResult = Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<TerraBase.Features.Shapes.ShapeErrorBody>;

namespace TerraBase.Features.Shapes;

internal static class ShapeRoute
{
    internal const string Shape = DatabaseRoute.Template + "/tables/{table}/shape";

    internal static readonly Validation BadTable = new("table must be an ordinary table name of at most 63 letters, digits and underscores");

    internal static readonly Validation BadFilter = new(
        $"where and columns are each at most once, non-empty, where at most {ShapeFilters.MaxWhere} and columns at most {ShapeFilters.MaxColumns} characters");

    internal static readonly Forbidden NotGranted = new("this token has no read grant for the table", "not_granted");

    internal static readonly string[] FilterParameters = ["where", "columns"];

    internal static ShapeQuery? Query(HttpContext context)
    {
        var query = context.Request.Query;
        string? where = null;
        string? columns = null;
        if (query.TryGetValue("where", out var wheres))
        {
            if (wheres.Count != 1 || wheres[0] is not { Length: > 0 and <= ShapeFilters.MaxWhere } text)
            {
                return null;
            }

            where = text;
        }

        if (query.TryGetValue("columns", out var lists))
        {
            if (lists.Count != 1 || lists[0] is not { Length: > 0 and <= ShapeFilters.MaxColumns } text)
            {
                return null;
            }

            columns = text;
        }

        return new ShapeQuery(where, columns);
    }

    internal static string QueryString(ShapeQuery query) =>
        (query.Where is { } where ? "&where=" + Uri.EscapeDataString(where) : "") + (query.Columns is { } columns ? "&columns=" + Uri.EscapeDataString(columns) : "");

    internal static async ValueTask<RouteResult> ResolveAsync(HttpContext context, ITenantDirectory tenants, IShapeLineages lineages,
        CancellationToken cancellationToken)
    {
        switch (await DatabaseRoute.CheckTenantAsync(context, tenants, cancellationToken))
        {
            case Validation error:
                return error;
            case NotFound error:
                return error;
            case Unavailable error:
                return error;
            case TenantContext database:
                var table = context.Request.RouteValues["table"] as string;
                if (!ShapeTables.IsValidName(table))
                {
                    return BadTable;
                }

                if (Query(context) is not { } query)
                {
                    return BadFilter;
                }

                if (context.Features.Get<ProjectAccess>() is { IsEndUser: true } access)
                {
                    if (!access.Tables.Contains(table!))
                    {
                        return NotGranted;
                    }

                    switch (await context.RequestServices.GetRequiredService<IShapeGrants>().GetGrantAsync(database, table!, cancellationToken))
                    {
                        case ShapeGrant grant:
                            string? bound = null;
                            if (grant.Where is { } rule && (bound = ShapeFilters.Bind(rule, access.Actor)) is null)
                            {
                                return NotGranted;
                            }

                            query = query with { Grant = new ShapeGrantRule(bound, grant.Columns is { } columns ? string.Join(',', columns) : null) };
                            break;
                        case NotFound:
                            return NotGranted;
                        case Unavailable error:
                            return error;
                    }
                }

                return await lineages.ResolveAsync(database, table!, query, cancellationToken) switch
                {
                    ResolvedShape shape => shape,
                    QuotaExceeded error => error,
                    Validation error => error,
                    Forbidden error => error,
                    NotFound error => error,
                    Unavailable error => error,
                };
            default:
                throw new UnreachableException();
        }
    }

    internal static IResult Error(RouteResult result) => result switch
    {
        Validation error => ErrorResponse.From(error),
        Forbidden error => ErrorResponse.From(error),
        NotFound error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
        QuotaExceeded error => ErrorResponse.From(error),
        _ => throw new UnreachableException(),
    };

    internal static IResult Error(ShapeRead read, HttpContext context, ShapeLineage lineage) => read switch
    {
        MustRefetch refetch => Refetch(context, refetch.Lineage ?? lineage, refetch.Message),
        QuotaExceeded error => ErrorResponse.From(error),
        NotFound error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
        _ => throw new UnreachableException(),
    };

    internal static RefetchResult Refetch(HttpContext context, ShapeLineage lineage, string message)
    {
        context.Response.Headers["TB-Shape-Handle"] = lineage.Handle;
        return TypedResults.Json(new ShapeErrorBody(new ShapeErrorDetail("must_refetch", message, lineage.Handle)),
            ShapeJsonContext.Default.ShapeErrorBody, statusCode: StatusCodes.Status409Conflict);
    }

    internal static long? Decimal(string text) =>
        text.Length is > 0 and <= 19 && (text == "0" || text[0] != '0') && text.All(char.IsAsciiDigit)
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    internal static Offset? CheckOffset(string text)
    {
        if (text == "-1")
        {
            return new Bootstrap();
        }

        if (Decimal(text) is { } at)
        {
            return new DecimalOffset(at);
        }

        return text.Split('.') is ["snap", var snapshot, var page] && Decimal(snapshot) is { } offset
            && Decimal(page) is { } index and <= int.MaxValue
            ? new SnapshotOffset(offset, (int)index)
            : null;
    }

    internal abstract record Offset;

    internal sealed record Bootstrap : Offset;

    internal sealed record SnapshotOffset(long Snapshot, int Page) : Offset;

    internal sealed record DecimalOffset(long Value) : Offset;
}

internal readonly union RouteResult(ResolvedShape, Validation, Forbidden, NotFound, Unavailable, QuotaExceeded);
