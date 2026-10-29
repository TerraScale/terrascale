using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13;

// Local producer payloads exercise the existing signed project-token HTTP scenario.
public sealed class AuditAnalyticsProducer
{
    public string Mode { get; set; } = "valid";
    public string DatabaseId { get; set; } = "";
    public int Reads { get; private set; }
    public JsonElement LastBody { get; private set; }
    public DateTimeOffset From { get; } = DateTimeOffset.FromUnixTimeSeconds((DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 + 1) * 60).AddHours(-1);

    public Task<IResult?> RespondAsync(HttpContext http)
    {
        if (!http.Request.Path.Value!.EndsWith("/analytics", StringComparison.Ordinal)) return Task.FromResult<IResult?>(null);
        Reads++;
        if (Mode == "offline") return Task.FromResult<IResult?>(Results.StatusCode(503));
        var projectId = http.Request.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
        object Sample(DateTimeOffset start, bool missingLatency = false, bool idle = false) => new
        {
            start = start.ToOffset(TimeSpan.FromHours(-3)),
            requests = new Dictionary<string, long> { ["GET"] = idle ? 0 : Mode == "missing" ? 9_007_199_254_740_993L : Mode == "large" ? 5_612_334_001L : 10 },
            operations = new Dictionary<string, long> { ["document_read"] = Mode == "large" ? 7_800_000_000_000L : 4 },
            errors = Mode == "large" ? 3_400_000L : 1,
            latency = new { p50Ms = missingLatency || idle || Mode == "missing" ? (double?)null : 0.5, p95Ms = missingLatency || idle || Mode == "missing" ? (double?)null : 1.75, p99Ms = missingLatency || idle || Mode == "missing" ? (double?)null : 9 },
            storageBytes = Mode == "missing" || idle ? (long?)null : 4096,
            clientBandwidth = new { bytesSent = 6, bytesReceived = 7 },
            replicationBandwidth = new { bytesSent = 8, bytesReceived = 9 }
        };
        var row = new
        {
            databaseId = DatabaseId,
            region = Mode == "foreign-region" ? "sfo" : "gru",
            series = Mode == "sparse" ? new[] { Sample(From), Sample(From.AddMinutes(2)) } :
                // The newest bucket is partial: no requests and no storage sample yet.
                Mode == "latest-partial" ? new[] { Sample(From), Sample(From.AddMinutes(1), idle: true) } :
                Mode == "explicit-gap" ? new[] { Sample(From), Sample(From.AddMinutes(1), missingLatency: true), Sample(From.AddMinutes(2)) } : new[] { Sample(From) }
        };
        LastBody = JsonSerializer.SerializeToElement(new
        {
            projectId,
            range = "1h",
            from = From.ToOffset(TimeSpan.FromHours(-3)),
            to = From.AddHours(1).ToOffset(TimeSpan.FromHours(-3)),
            bucketSeconds = 60,
            items = Mode == "duplicate" ? new[] { row, row } : new[] { row }
        });
        return Task.FromResult<IResult?>(Results.Json(LastBody));
    }
}
