using System.Text.Json.Serialization;

namespace TerraBase.Features.Pressure;

public sealed record PressureBody
{
    [JsonPropertyName("schema")] public int Schema => 1;
    [JsonPropertyName("node")] public required string Node { get; init; }
    [JsonPropertyName("local_ready")] public bool? LocalReady { get; init; }
    [JsonPropertyName("state")] public string State => "unknown";
    [JsonPropertyName("process_id")] public required string ProcessId { get; init; }
    [JsonPropertyName("sampled_at")] public required DateTimeOffset SampledAt { get; init; }
    [JsonPropertyName("release")] public required string Release { get; init; }
    [JsonPropertyName("region")] public required string Region { get; init; }
    [JsonPropertyName("sample_window_seconds")] public double SampleWindowSeconds { get; init; }
    [JsonPropertyName("cpu_util")] public double? CpuUtil { get; init; }
    [JsonPropertyName("mem_util")] public double? MemoryUtil { get; init; }
    [JsonPropertyName("memory_used_bytes")] public long? MemoryUsedBytes { get; init; }
    [JsonPropertyName("memory_budget_bytes")] public long MemoryBudgetBytes { get; init; }
    [JsonPropertyName("disk_used_bytes")] public long? DiskUsedBytes { get; init; }
    [JsonPropertyName("disk_free_bytes")] public long? DiskFreeBytes { get; init; }
    [JsonPropertyName("disk_total_bytes")] public long? DiskTotalBytes { get; init; }
    [JsonPropertyName("disk_iops")] public double? DiskIops => null;
    [JsonPropertyName("request_rate")] public double RequestRate { get; init; }
    [JsonPropertyName("io_queue_depth")] public double? IoQueueDepth => null;
    [JsonPropertyName("io_admitted_count")] public long? IoAdmittedCount { get; init; }
    [JsonPropertyName("p95_latency_ms")] public double P95LatencyMs { get; init; }
    [JsonPropertyName("load")] public required PressureLoad Load { get; init; }
    [JsonPropertyName("listeners")] public required IReadOnlyList<ListenerPressure> Listeners { get; init; }
    [JsonPropertyName("top_organizations")] public required IReadOnlyList<TenantPressure> TopTenants { get; init; }
    [JsonPropertyName("untracked_tenant_requests")] public long UntrackedTenantRequests { get; init; }
    [JsonPropertyName("data_bytes")] public long? DataBytes { get; init; }
    [JsonPropertyName("storage_sampled_at")] public DateTimeOffset? StorageSampledAt { get; init; }
    [JsonPropertyName("top_organizations_by_storage")] public required IReadOnlyList<TenantStoragePressure> TopTenantsByStorage { get; init; }
}
public sealed record PressureLoad(
    [property: JsonPropertyName("write_qps")] double WriteQps,
    [property: JsonPropertyName("connections")] long Connections)
{
    [JsonPropertyName("storage_bytes")] public long? StorageBytes => null;
}
public sealed record ListenerPressure(
    [property: JsonPropertyName("listener")] string Listener,
    [property: JsonPropertyName("request_rate")] double RequestRate,
    [property: JsonPropertyName("p95_latency_ms")] double P95LatencyMs,
    [property: JsonPropertyName("requests_total")] long RequestsTotal);
public sealed record TenantPressure(
    [property: JsonPropertyName("org")] string Org,
    [property: JsonPropertyName("request_rate")] double RequestRate,
    [property: JsonPropertyName("plan")] string? Plan = null)
{
    [JsonPropertyName("cpu_share")] public double? CpuShare => null;
}
public sealed record TenantStoragePressure(
    [property: JsonPropertyName("org")] string Org,
    [property: JsonPropertyName("bytes")] long Bytes);
