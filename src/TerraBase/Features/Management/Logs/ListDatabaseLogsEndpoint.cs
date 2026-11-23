using System.Text.Json.Serialization;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Logs;

public sealed record LogsInput(string? Project, string? Database, string? After, int? Limit);

public sealed record LogsBody(IReadOnlyList<RequestLogEntry> Items, string? Next);

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LogsInput))]
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
        Description("Seven days of request logs: method, path, status, duration and region. Query: after (exclusive cursor), limit (1 to 100).");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<LogsBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(LogsInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementService.Limit(request.Limit) switch
        {
            int limit => await access.ManagedAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken) switch
            {
                ResolvedDatabase resolved => await logs.ListAsync(resolved.Database.Id, request.After, limit, cancellationToken) switch
                {
                    IReadOnlyList<RequestLogEntry> entries => new LogsBody(entries, entries.Count == limit ? entries[^1].Id : null),
                    Unavailable unavailable => unavailable,
                },
                Validation invalid => invalid,
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
        };
    }
}
