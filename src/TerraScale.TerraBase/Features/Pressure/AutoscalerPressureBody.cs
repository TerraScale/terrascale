using System.Text.Json.Serialization;

namespace TerraBase.Features.Pressure;

/// <summary>Schema-2 node evidence; unknown lifecycle and safety counters remain explicit.</summary>
/// <param name="sample">An immutable cached telemetry sample.</param>
public sealed class AutoscalerPressureBody(PressureBody sample)
{
    /// <summary>Instance-bound node evidence version.</summary>
    [JsonPropertyName("schema")] public int Schema => 2;
    /// <inheritdoc cref="PressureBody.Node" />
    [JsonPropertyName("node")] public string Node => sample.Node;
    /// <inheritdoc cref="PressureBody.InstanceId" />
    [JsonPropertyName("instance_id")] public string? InstanceId => sample.InstanceId;
    /// <inheritdoc cref="PressureBody.State" />
    [JsonPropertyName("state")] public string State => sample.State;
    /// <inheritdoc cref="PressureBody.ProcessId" />
    [JsonPropertyName("process_id")] public string ProcessId => sample.ProcessId;
    /// <inheritdoc cref="PressureBody.SampledAt" />
    [JsonPropertyName("sampled_at")] public DateTimeOffset SampledAt => sample.SampledAt;
    /// <inheritdoc cref="PressureBody.Release" />
    [JsonPropertyName("release")] public string Release => sample.Release;
    /// <inheritdoc cref="PressureBody.Region" />
    [JsonPropertyName("region")] public string Region => sample.Region;
    /// <inheritdoc cref="PressureBody.SampleWindowSeconds" />
    [JsonPropertyName("sample_window_seconds")] public double SampleWindowSeconds => sample.SampleWindowSeconds;
    /// <inheritdoc cref="PressureBody.CpuUtil" />
    [JsonPropertyName("cpu_util")] public double? CpuUtil => sample.CpuUtil;
    /// <inheritdoc cref="PressureBody.MemoryUtil" />
    [JsonPropertyName("mem_util")] public double? MemoryUtil => sample.MemoryUtil;
    /// <inheritdoc cref="PressureBody.DiskUsedBytes" />
    [JsonPropertyName("disk_used_bytes")] public long? DiskUsedBytes => sample.DiskUsedBytes;
    /// <inheritdoc cref="PressureBody.DiskTotalBytes" />
    [JsonPropertyName("disk_total_bytes")] public long? DiskTotalBytes => sample.DiskTotalBytes;
    /// <inheritdoc cref="PressureBody.DiskIops" />
    [JsonPropertyName("disk_iops")] public double? DiskIops => sample.DiskIops;
    /// <inheritdoc cref="PressureBody.RequestRate" />
    [JsonPropertyName("request_rate")] public double RequestRate => sample.RequestRate;
    /// <inheritdoc cref="PressureBody.IoQueueDepth" />
    [JsonPropertyName("io_queue_depth")] public double? IoQueueDepth => sample.IoQueueDepth;
    /// <inheritdoc cref="PressureBody.IoAdmittedCount" />
    [JsonPropertyName("io_admitted_count")] public long? IoAdmittedCount => sample.IoAdmittedCount;
    /// <inheritdoc cref="PressureBody.P95LatencyMs" />
    [JsonPropertyName("p95_latency_ms")] public double P95LatencyMs => sample.P95LatencyMs;
    /// <inheritdoc cref="PressureBody.Load" />
    [JsonPropertyName("load")] public PressureLoad Load => sample.Load;
    /// <inheritdoc cref="PressureBody.LocalReady" />
    [JsonPropertyName("local_ready")] public bool? LocalReady => sample.LocalReady;
    /// <inheritdoc cref="PressureBody.SenderDurableLagSeq" />
    [JsonPropertyName("sender_durable_lag_seq")] public long? SenderDurableLagSeq => sample.SenderDurableLagSeq;
}
