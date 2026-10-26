using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>One customer-bucket NDJSON export attempt, separate from snapshot backups.</summary>
/// <param name="StartedAt">Attempt start; determines the next cron occurrence.</param>
/// <param name="FinishedAt">Attempt completion time.</param>
/// <param name="Status">succeeded or failed; preserve future producer outcomes.</param>
/// <param name="Key">Customer-bucket object key on success, otherwise null.</param>
/// <param name="Documents">Documents written before completion or failure.</param>
/// <param name="Bytes">Uploaded bytes; zero on failure.</param>
/// <param name="Error">Producer failure text, without bucket responses or credentials; null on success.</param>
public sealed record TerraBaseExportRun(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Status,
    string? Key, long Documents, long Bytes, string? Error);

/// <summary>A database's customer-bucket export schedule.</summary>
/// <param name="Cron">Standard five-field UTC cron; null when unscheduled.</param>
/// <param name="UpdatedAt">Last save time, or null before any save.</param>
/// <param name="LastRun">Last attempted run, or null before the first.</param>
public sealed record TerraBaseExportSchedule(string? Cron, DateTimeOffset? UpdatedAt, TerraBaseExportRun? LastRun);

internal sealed record TerraBaseExportScheduleRequest(string? Cron);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = TerraBaseJsonContext.MaxResponseDepth,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TerraBaseExportSchedule))]
[JsonSerializable(typeof(TerraBaseExportScheduleRequest))]
internal sealed partial class TerraBaseExportScheduleJsonContext : JsonSerializerContext;

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Reads GET …/export-schedule, including the unsaved null schedule and the last attempt.
    /// Requires databases:read project authority. No request replay or background polling.
    /// </summary>
    public async Task<TerraBaseExportSchedule> GetExportScheduleAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/export-schedule", null,
            TerraBaseExportScheduleJsonContext.Default.TerraBaseExportSchedule, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Saves PUT …/export-schedule. Requires databases:write project authority; sent once.
    /// A nonempty five-field UTC cron requires a configured project backup destination (409 otherwise).
    /// Null, empty or whitespace cron removes the schedule while retaining the last attempt.
    /// Runs write live documents as importable NDJSON to terrabase-exports/{project}/{database}/{time}.ndjson.
    /// Cron validation and normalization belong to the producer; invalid cron returns 400.
    /// </summary>
    public async Task<TerraBaseExportSchedule> SetExportScheduleAsync(string? cron, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, path + "/export-schedule",
            TerraBaseConnection.Json(new TerraBaseExportScheduleRequest(cron), TerraBaseExportScheduleJsonContext.Default.TerraBaseExportScheduleRequest),
            TerraBaseExportScheduleJsonContext.Default.TerraBaseExportSchedule, null, cancellationToken).ConfigureAwait(false)).Value;
}
