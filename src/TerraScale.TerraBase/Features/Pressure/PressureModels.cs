using System.Text.Json.Serialization;

namespace TerraBase.Features.Pressure;

/// <summary>One cached pressure sample. Null means unavailable, never zero load.</summary>
public sealed record PressureBody
{
    /// <summary>Wire schema version.</summary>
    [JsonPropertyName("schema")] public int Schema => 2;
    /// <summary>Operator node identity.</summary>
    [JsonPropertyName("node")] public required string Node { get; init; }
    /// <summary>Cloud instance identity supplied by the operator; null when unconfigured.</summary>
    [JsonPropertyName("instance_id")] public string? InstanceId { get; init; }
    /// <summary>Local serving readiness using the same checks as /readyz, not member catch-up.</summary>
    [JsonPropertyName("local_ready")] public bool? LocalReady { get; init; }
    /// <summary>Outgoing local-origin groups not durably acknowledged by configured peers; not incoming lag.</summary>
    [JsonPropertyName("sender_durable_lag_seq")] public long? SenderDurableLagSeq { get; init; }
    /// <summary>Membership readiness is not implemented.</summary>
    [JsonPropertyName("state")] public string State => "unknown";
    /// <summary>Unique identifier for this host incarnation.</summary>
    [JsonPropertyName("process_id")] public required string ProcessId { get; init; }
    /// <summary>UTC measurement time, independent of fetch time.</summary>
    [JsonPropertyName("sampled_at")] public required DateTimeOffset SampledAt { get; init; }
    /// <summary>Full informational build version, including revision metadata.</summary>
    [JsonPropertyName("release")] public required string Release { get; init; }
    /// <summary>Configured node region.</summary>
    [JsonPropertyName("region")] public required string Region { get; init; }
    /// <summary>Elapsed seconds represented by interval counters.</summary>
    [JsonPropertyName("sample_window_seconds")] public double SampleWindowSeconds { get; init; }
    /// <summary>Process CPU time divided by elapsed time and available logical processors.</summary>
    [JsonPropertyName("cpu_util")] public double? CpuUtil { get; init; }
    /// <summary>Working set divided by the configured budget, capped at one.</summary>
    [JsonPropertyName("mem_util")] public double? MemoryUtil { get; init; }
    /// <summary>Process resident working set in bytes.</summary>
    [JsonPropertyName("memory_used_bytes")] public long? MemoryUsedBytes { get; init; }
    /// <summary>Configured telemetry budget in bytes; currently not enforced by admission.</summary>
    [JsonPropertyName("memory_budget_bytes")] public long MemoryBudgetBytes { get; init; }
    /// <summary>Used bytes of the filesystem containing the data directory.</summary>
    [JsonPropertyName("disk_used_bytes")] public long? DiskUsedBytes { get; init; }
    /// <summary>Free bytes of that filesystem, including privileged reserved space.</summary>
    [JsonPropertyName("disk_free_bytes")] public long? DiskFreeBytes { get; init; }
    /// <summary>Total bytes of that filesystem.</summary>
    [JsonPropertyName("disk_total_bytes")] public long? DiskTotalBytes { get; init; }
    /// <summary>Device IOPS is not currently measured.</summary>
    [JsonPropertyName("disk_iops")] public double? DiskIops => null;
    /// <summary>Completed HTTP requests per second across all listeners.</summary>
    [JsonPropertyName("request_rate")] public double RequestRate { get; init; }
    /// <summary>I/O admission queue measurement is unavailable until #13.</summary>
    [JsonPropertyName("io_queue_depth")] public double? IoQueueDepth => null;
    /// <summary>Current admitted storage operations; null when storage admission is closed.</summary>
    [JsonPropertyName("io_admitted_count")] public long? IoAdmittedCount { get; init; }
    /// <summary>Approximate interval p95 HTTP latency in milliseconds.</summary>
    [JsonPropertyName("p95_latency_ms")] public double P95LatencyMs { get; init; }
    /// <summary>Member load counters; unavailable fields remain explicit null.</summary>
    [JsonPropertyName("load")] public required PressureLoad Load { get; init; }
    /// <summary>Per-listener completed request rates, latencies and cumulative counts.</summary>
    [JsonPropertyName("listeners")] public required IReadOnlyList<ListenerPressure> Listeners { get; init; }
    /// <summary>Top ten observed organizations by authenticated API-key request rate.</summary>
    [JsonPropertyName("top_organizations")] public required IReadOnlyList<TenantPressure> TopTenants { get; init; }
    /// <summary>Requests omitted from tenant ranking when its bounded tracking table is full.</summary>
    [JsonPropertyName("untracked_tenant_requests")] public long UntrackedTenantRequests { get; init; }
    /// <summary>Cached physical bytes of tenant database files and their sidecars.</summary>
    [JsonPropertyName("data_bytes")] public long? DataBytes { get; init; }
    /// <summary>UTC time of the independent storage file-metadata sample.</summary>
    [JsonPropertyName("storage_sampled_at")] public DateTimeOffset? StorageSampledAt { get; init; }
    /// <summary>Top ten organizations by cached physical database file bytes.</summary>
    [JsonPropertyName("top_organizations_by_storage")] public required IReadOnlyList<TenantStoragePressure> TopTenantsByStorage { get; init; }
}

/// <summary>Operational member load; unknown fields are not lifecycle evidence.</summary>
/// <param name="WriteQps">Successfully committed client write transactions per second.</param>
/// <param name="Connections">Current open client transport connections, including TLS handshakes.</param>
public sealed record PressureLoad(
    [property: JsonPropertyName("write_qps")] double WriteQps,
    [property: JsonPropertyName("connections")] long Connections)
{
    /// <summary>Logical storage size is not tracked; physical size is reported separately.</summary>
    [JsonPropertyName("storage_bytes")] public long? StorageBytes => null;
    /// <summary>Authenticated peer applied-frontier lag is unavailable until #11.</summary>
    [JsonPropertyName("replication_lag_seq")] public long? ReplicationLagSeq => null;
}

/// <summary>One listener's HTTP request sample.</summary>
/// <param name="Listener">Administrative, client or peer listener.</param>
/// <param name="RequestRate">Completed requests per second.</param>
/// <param name="P95LatencyMs">Approximate p95 milliseconds over the same interval.</param>
/// <param name="RequestsTotal">Cumulative completed HTTP requests.</param>
public sealed record ListenerPressure(
    [property: JsonPropertyName("listener")] string Listener,
    [property: JsonPropertyName("request_rate")] double RequestRate,
    [property: JsonPropertyName("p95_latency_ms")] double P95LatencyMs,
    [property: JsonPropertyName("requests_total")] long RequestsTotal);

/// <summary>One organization's request rate. CPU attribution is unavailable.</summary>
/// <param name="Org">Organization ID from an authenticated API key.</param>
/// <param name="RequestRate">Completed requests per second.</param>
/// <param name="Plan">Last plan resolved by an endpoint in this interval, or null when unresolved.</param>
public sealed record TenantPressure(
    [property: JsonPropertyName("org")] string Org,
    [property: JsonPropertyName("request_rate")] double RequestRate,
    [property: JsonPropertyName("plan")] string? Plan = null)
{
    /// <summary>CPU time cannot be inferred from request shares or elapsed durations.</summary>
    [JsonPropertyName("cpu_share")] public double? CpuShare => null;
}

/// <summary>Physical storage of one organization.</summary>
/// <param name="Org">Organization ID.</param>
/// <param name="Bytes">SQLite file and sidecar bytes, not logical document size.</param>
public sealed record TenantStoragePressure(
    [property: JsonPropertyName("org")] string Org,
    [property: JsonPropertyName("bytes")] long Bytes);
