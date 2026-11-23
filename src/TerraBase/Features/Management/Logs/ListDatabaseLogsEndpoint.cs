using System.Globalization;
using System.Text.Json.Serialization;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Logs;

public sealed record LogsInput(string? Project, string? Database, string? After, int? Limit, string? Status);

public sealed record InternalLogInput(string? Project, string? Database, string? Id, string? Method, string? Path, int Status, long DurationMs);

public sealed record LogsBody(IReadOnlyList<RequestLogEntry> Items, string? Next, bool Gap = false);

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LogsInput))]
[JsonSerializable(typeof(InternalLogInput))]
[JsonSerializable(typeof(LogsBody))]
internal sealed partial class LogsJsonContext : JsonSerializerContext;

internal sealed class ListDatabaseLogsEndpoint(DatabaseAccess access, RequestLogRecorder logs)
    : Endpoint<LogsInput, Result<LogsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(ManagementRoute.Database + "/logs");
        Name("listDatabaseLogs");
        Summary("Lists the database's recent requests in this region");
        Description("Seven days of request logs: id (the request's X-Request-ID), method, path, status, duration and region. Without after, the most recent requests come first "
            + "and next is the oldest id when the page is full. With after (exclusive cursor), requests come in id order and next is the cursor to continue from. Query: limit (1 to 100), status (2xx, 3xx, 4xx, 5xx, error, or exact code).");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<LogsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(LogsInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryParseStatus(request.Status, out var minStatus, out var maxStatus))
        {
            return new Validation("status must be 2xx, 3xx, 4xx, 5xx, error, or an HTTP status code", "invalid_request");
        }

        var checkedLimit = ManagementService.Limit(request.Limit);
        return checkedLimit switch
        {
            int limit => await QueryAsync(request, limit, minStatus, maxStatus, cancellationToken),
            Validation invalid => invalid,
        };
    }

    private async ValueTask<Result<LogsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> QueryAsync(
        LogsInput request, int limit, int? minStatus, int? maxStatus, CancellationToken cancellationToken)
    {
        var resolvedOutcome = await access.ManagedAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken);
        return resolvedOutcome switch
        {
            ResolvedDatabase resolved => await ListEntriesAsync(resolved.Database.Id, request.After, limit, minStatus, maxStatus, cancellationToken),
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<LogsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ListEntriesAsync(
        Ulid databaseId, string? after, int limit, int? minStatus, int? maxStatus, CancellationToken cancellationToken)
    {
        var listed = await logs.ListAsync(databaseId, after, limit, minStatus, maxStatus, cancellationToken);
        return listed switch
        {
            IReadOnlyList<RequestLogEntry> entries => new LogsBody(entries, Next(entries, after, limit), logs.HasGap(after)),
            Unavailable unavailable => unavailable,
        };
    }

    private static bool TryParseStatus(string? raw, out int? minStatus, out int? maxStatus)
    {
        minStatus = null;
        maxStatus = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "2xx":
                (minStatus, maxStatus) = (200, 299);
                return true;
            case "3xx":
                (minStatus, maxStatus) = (300, 399);
                return true;
            case "4xx":
                (minStatus, maxStatus) = (400, 499);
                return true;
            case "5xx":
                (minStatus, maxStatus) = (500, 599);
                return true;
            case "error":
                (minStatus, maxStatus) = (400, 599);
                return true;
            default:
                if (int.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out var exact) && exact is >= 100 and <= 599)
                {
                    (minStatus, maxStatus) = (exact, exact);
                    return true;
                }

                return false;
        }
    }

    private static string? Next(IReadOnlyList<RequestLogEntry> entries, string? after, int limit) => after switch
    {
        null => entries.Count == limit ? entries[^1].Id : null,
        _ => entries.Count > 0 ? entries[^1].Id : after,
    };
}

internal sealed class RecordInternalLogEndpoint(CatalogStore catalog, RequestLogRecorder logs)
    : Endpoint<InternalLogInput, Result<Done, Validation, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Post("/internal/logs");
        Name("recordInternalLog");
        Summary("Records a platform request log entry for a database");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<Done, Validation, Forbidden, NotFound, Unavailable>> HandleAsync(InternalLogInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "admin"))
        {
            return new Forbidden("recording internal logs needs an operator credential", "forbidden_caller");
        }

        if (ManagementService.Id(request.Project) is not { } projectId || string.IsNullOrWhiteSpace(request.Database)
            || request.Method is not { Length: > 0 } method || request.Path is not { Length: > 0 } path || request.Status is < 100 or > 599)
        {
            return new Validation("invalid internal log entry", "invalid_request");
        }

        var found = await catalog.FindDatabaseAsync(projectId, request.Database, cancellationToken);
        return found switch
        {
            DatabaseRecord database => Record(database.Id, request.Id, method, path, request.Status, request.DurationMs),
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private Done Record(Ulid databaseId, string? id, string method, string path, int status, long durationMs)
    {
        logs.Enqueue(databaseId, id, method, path, status, durationMs);
        return new Done();
    }
}
