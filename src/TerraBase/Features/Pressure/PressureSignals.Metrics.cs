using System.Diagnostics.Metrics;

namespace TerraBase.Features.Pressure;

internal sealed partial class PressureSignals
{
    private void RegisterMetrics(Meter meter)
    {
        Gauge("cpu_util", snapshot => snapshot.CpuUtil);
        Gauge("mem_util", snapshot => snapshot.MemoryUtil);
        Gauge("memory_used_bytes", snapshot => snapshot.MemoryUsedBytes);
        Gauge("memory_budget_bytes", snapshot => snapshot.MemoryBudgetBytes);
        Gauge("disk_used_bytes", snapshot => snapshot.DiskUsedBytes);
        Gauge("disk_free_bytes", snapshot => snapshot.DiskFreeBytes);
        Gauge("disk_total_bytes", snapshot => snapshot.DiskTotalBytes);
        Gauge("request_rate", snapshot => snapshot.RequestRate);
        Gauge("p95_latency_ms", snapshot => snapshot.P95LatencyMs);
        Gauge("data_bytes", snapshot => snapshot.DataBytes);
        Gauge("write_qps", snapshot => snapshot.Load.WriteQps);
        Gauge("connections", snapshot => snapshot.Load.Connections);
        Gauge("io_admitted_count", snapshot => snapshot.IoAdmittedCount);
        Gauge("local_ready", snapshot => snapshot.LocalReady is { } ready ? (ready ? 1 : 0) : null);
        Gauge("sampled_at_seconds", snapshot => snapshot.SampledAt.ToUnixTimeMilliseconds() / 1000.0);
        Gauge("storage_sampled_at_seconds", snapshot => snapshot.StorageSampledAt?.ToUnixTimeMilliseconds() / 1000.0);
        Gauge("sample_window_seconds", snapshot => snapshot.SampleWindowSeconds);
        Gauge("untracked_tenant_requests", snapshot => snapshot.UntrackedTenantRequests);
        meter.CreateObservableGauge("terrabase_pressure_info", () => new Measurement<int>(1,
            new("node", _options.MachineId), new("region", _options.Region), new("process_id", _processId), new("release", _release), new("state", "unknown")));
        meter.CreateObservableGauge("terrabase_pressure_listener_request_rate", () => ListenerMeasurements(listener => listener.RequestRate));
        meter.CreateObservableGauge("terrabase_pressure_listener_p95_latency_ms", () => ListenerMeasurements(listener => listener.P95LatencyMs));
        meter.CreateObservableCounter("terrabase_pressure_listener_requests", () => ListenerMeasurements(listener => listener.RequestsTotal));
        meter.CreateObservableGauge("terrabase_pressure_top_tenant_request_rate", () =>
        {
            var tenants = Snapshot.TopTenants;
            return Enumerable.Range(0, 10).Select(index => new Measurement<double>(index < tenants.Count ? tenants[index].RequestRate : 0,
                new KeyValuePair<string, object?>("rank", index + 1)));
        });
        meter.CreateObservableGauge("terrabase_pressure_top_tenant_storage_bytes", () =>
        {
            var snapshot = Snapshot;
            return Enumerable.Range(0, 10).Select(index => new Measurement<double>(snapshot.DataBytes is null ? double.NaN
                : index < snapshot.TopTenantsByStorage.Count ? snapshot.TopTenantsByStorage[index].Bytes : 0,
                new KeyValuePair<string, object?>("rank", index + 1)));
        });
        meter.CreateObservableGauge("terrabase_pressure_available", () =>
        {
            var snapshot = Snapshot;
            return new[]
            {
                Available("cpu_util", snapshot.CpuUtil.HasValue),
                Available("mem_util", snapshot.MemoryUtil.HasValue),
                Available("disk", snapshot.DiskTotalBytes.HasValue),
                Available("data_bytes", snapshot.DataBytes.HasValue),
                Available("disk_iops", false),
                Available("io_queue_depth", false),
                Available("io_admitted_count", snapshot.IoAdmittedCount.HasValue),
                Available("connections", true),
                Available("storage_bytes", false),
                Available("tenant_cpu_share", false),
                Available("local_ready", snapshot.LocalReady.HasValue),
            };
        });

        void Gauge(string name, Func<PressureBody, double?> value)
        {
            meter.CreateObservableGauge("terrabase_pressure_" + name, () => value(Snapshot) ?? double.NaN);
        }
    }

    private IEnumerable<Measurement<double>> ListenerMeasurements(Func<ListenerPressure, double> value) =>
        Snapshot.Listeners.Select(listener => new Measurement<double>(value(listener), new KeyValuePair<string, object?>("listener", listener.Listener)));

    private static Measurement<int> Available(string signal, bool available) =>
        new(available ? 1 : 0, new KeyValuePair<string, object?>("signal", signal));
}
