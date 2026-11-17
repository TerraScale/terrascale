using System.Globalization;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Shapes;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Rows;

internal sealed record RowsRouteServices(ITenantDirectory Tenants, IReplicaCatalog Placements, IRegionPeers Peers, TerraBaseOptions Options, RowsService Rows);

internal static class RowsRoute
{
    internal const string Rows = DatabaseRoute.Template + "/tables/{table}/rows";
    internal const string Row = Rows + "/{key}";
    internal const string Batch = DatabaseRoute.Template + "/rows:batch";
    internal const int MaxBytes = 1024 * 1024;
    internal const int MaxOperations = 100;

    internal const string Description =
        " Values are null, finite numbers, booleans (1 or 0), strings, or blobs as {\"base64\":\"…\"}; the body is UTF-8 JSON of at most 1 MiB. "
        + "An end user signed in to the project's tenant is checked by the database's rules inside the home write transaction: the operation's rule, "
        + "writable and set columns, and validate, all evaluated on the row as stored. The answer's row is cut to the rule's columns, or null when read is false. "
        + "Project credentials write every row and column. Writes run in the database's home region: a region holding a replica forwards the request there, "
        + "and any other region answers 421 wrong_region with nothing applied. Writes count against the monthly write allowance like SQL writes.";

    internal static async ValueTask<IResult> ServeAsync(HttpContext context, RowsRouteServices services, bool body, bool batch,
        Func<byte[]?, object> operations, Func<IReadOnlyList<RowAnswer>, IResult> answer, CancellationToken cancellationToken)
    {
        if (DatabaseRoute.CheckParameters(context) is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        return await DatabaseRoute.TenantAsync(context, services.Tenants, async (database, token) =>
        {
            var access = context.Features.Get<ProjectAccess>();
            if (access is { IsEndUser: true, Auth: null })
            {
                return ErrorResponse.From(new Forbidden("row writes need a token from the project's tenant"));
            }

            DatabasePlacement placement;
            switch (await services.Placements.FindAsync(database, token).ConfigureAwait(false))
            {
                case TenantDatabaseRecord record:
                    placement = record.Placement;
                    break;
                case NotFound:
                    return ErrorResponse.From(DatabaseRoute.UnknownDatabase);
                case Unavailable error:
                    return ErrorResponse.From(error);
                default:
                    throw new InvalidOperationException("the placement lookup answered an unexpected result");
            }

            byte[]? json = null;
            if (body)
            {
                switch (await DatabaseRoute.CheckBodyAsync(context, MaxBytes, "invalid_request", token, DatabaseRoute.JsonMediaType).ConfigureAwait(false))
                {
                    case byte[] read:
                        json = read;
                        break;
                    case Validation error:
                        return ErrorResponse.From(error);
                    case PayloadTooLarge error:
                        return ErrorResponse.From(error);
                    case UnsupportedMediaType error:
                        return ErrorResponse.From(error);
                }
            }

            var region = services.Options.Region;
            if (placement.Home != region)
            {
                var peer = Listeners.IsForwarded(context) || !placement.PlacedIn(region)
                    ? null
                    : (await services.Peers.OtherRegionsAsync(token).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Region == placement.Home);
                if (peer is null)
                {
                    return DatabaseRegions.HomeRegion(placement.Home, placement.Regions);
                }

                await HomeForwarding.ForwardAsync(context, new HomeNode(peer.Region, peer.Address), json).ConfigureAwait(false);
                return Results.Empty;
            }

            var parsed = operations(json);
            if (parsed is RowRefused refusedRequest)
            {
                return Error(refusedRequest);
            }

            return await services.Rows.WriteAsync(database, access?.Auth, (IReadOnlyList<RowOperation>)parsed, batch, token).ConfigureAwait(false) switch
            {
                IReadOnlyList<RowAnswer> answers => answer(answers),
                RowRefused refused => Error(refused),
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static object Table(HttpContext context) =>
        context.Request.RouteValues["table"] is string table && ShapeTables.IsValidName(table)
            ? table
            : new RowRefused(StatusCodes.Status404NotFound, "unknown_table", "the database has no such table");

    internal static object Key(string? key) =>
        key is { Length: > 0 and <= 20 } && long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : Invalid("key is a row id: one whole number");

    internal static object Values(IReadOnlyDictionary<string, JsonElement>? row)
    {
        if (row is null)
        {
            return Invalid("row must be an object of column values");
        }

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (var (column, element) in row)
        {
            if (SqlJson.Value(element) is not SqlValue value)
            {
                return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request",
                    $"{column}: values are null, finite numbers, booleans, strings or {{\"base64\": …}} blobs", new RowsErrorDetails(Column: column));
            }

            values[column] = SqlSession.Bound(value);
        }

        return values;
    }

    internal static RowRefused Invalid(string message) => new(StatusCodes.Status400BadRequest, "invalid_request", message);

    internal static object Single(byte[]? json, Func<IReadOnlyDictionary<string, object?>, RowOperation> operation) =>
        RequestJson.Read<RowBody>(json, "invalid_request") switch
        {
            Validation error => Invalid(error.Message),
            RowBody parsed => Values(parsed.Row) switch
            {
                IReadOnlyDictionary<string, object?> values => (IReadOnlyList<RowOperation>)[operation(values)],
                var refused => refused,
            },
        };

    internal static IResult Error(RowRefused refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        return new Refusal(refused);
    }

    private sealed class Refusal(RowRefused refused) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return TypedResults.Json(new RowsErrorBody(new RowsError(refused.Code, refused.Message, refused.Details)), RowsJsonContext.Default.RowsErrorBody,
                statusCode: refused.Status).ExecuteAsync(httpContext);
        }
    }
}
