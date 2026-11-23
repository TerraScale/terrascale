using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.Health;

internal readonly union ReadinessResponse(StatusBody, UnavailableStatus<StatusBody>);

internal readonly union BackupHealthResponse(BackupBody, UnavailableStatus<BackupBody>);

public sealed record StatusBody(
    string Status,
    string Version,
    string Region,
    string? StorageFormat = null,
    string? Revision = null,
    string? Machine = null,
    string? Code = null,
    string? Detail = null);

public sealed record BackupBody(
    string Status,
    string Region,
    string? Code,
    string? Message,
    string? ArchivedThrough,
    DateTimeOffset? EarliestRestorePoint,
    DateTimeOffset? LatestRestorePoint,
    DateTimeOffset? LastSnapshotAt,
    long? ArchivedBytes,
    int? PendingSegments);

public sealed class UnavailableStatus<T>(T body, JsonSerializerContext context) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.RetryAfter = "1";
        return httpContext.Response.WriteAsJsonAsync(body, typeof(T), context, contentType: null, httpContext.RequestAborted);
    }
}

internal sealed class NodeReadiness
{
    private volatile string? _blocked = "starting";

    public string? Blocked => _blocked;

    public void Ready() => _blocked = null;

    public void Block(string reason) => _blocked = reason;
}

internal static class HealthStatus
{
    public static StatusBody For(HttpContext context, string status, TerraBaseOptions options, string? code = null, string? detail = null) =>
        Listeners.ArrivedOn(context) == TerraBaseListener.Internal
            ? new(status, ServiceVersion.Value, options.Region, StorageFormat.Id, ServiceVersion.Revision, options.MachineId, code, detail)
            : new(status, ServiceVersion.Value, options.Region, Code: code);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusBody))]
[JsonSerializable(typeof(BackupBody))]
internal sealed partial class HealthJsonContext : JsonSerializerContext;
