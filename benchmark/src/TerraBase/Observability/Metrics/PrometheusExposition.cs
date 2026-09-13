using System.Globalization;
using System.Text;

namespace TerraBase;

/// <summary>The private Prometheus endpoint contract.</summary>
public static class PrometheusMetrics
{
    /// <summary>The endpoint path owned by a TerraBase host.</summary>
    public const string Path = "/internal/metrics";

    /// <summary>The Prometheus 0.0.4 response content type.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>The maximum exposition body accepted by the engine.</summary>
    public const int MaxBodyBytes = 512 * 1024;

    /// <summary>The fixed request latency bucket boundaries, in seconds.</summary>
    public static readonly double[] RequestLatencyBuckets =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    /// <summary>Renders a metrics snapshot as Prometheus text.</summary>
    /// <param name="snapshot">The immutable snapshot.</param>
    /// <returns>The exposition body.</returns>
    public static string Render(MetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.RenderPrometheus();
    }
}

/// <summary>An immutable, internally consistent metrics snapshot.</summary>
public sealed partial class MetricsSnapshot
{
    /// <summary>Renders this snapshot in Prometheus text format.</summary>
    /// <returns>The exposition body.</returns>
    public string RenderPrometheus()
    {
        var output = new StringBuilder();
        Help(output, "terrabase_liveness", "Whether the TerraBase process is live.", "gauge");
        output.Append("terrabase_liveness ").Append(IsLive ? '1' : '0').Append('\n');
        Help(output, "terrabase_started", "Whether the TerraBase process reached serving.", "gauge");
        output.Append("terrabase_started ").Append(Started ? '1' : '0').Append('\n');
        Help(output, "terrabase_ready", "Whether required TerraBase components are healthy.", "gauge");
        output.Append("terrabase_ready ").Append(Ready ? '1' : '0').Append('\n');

        Help(output, "terrabase_requests_total", "Completed TerraBase requests.", "counter");
        Help(output, "terrabase_request_duration_seconds", "TerraBase request duration.", "histogram");
        foreach (var series in Requests)
        {
            var labels = $"region=\"{Label(series.Region)}\",operation=\"{Label(series.Operation)}\",outcome=\"{Label(series.Outcome)}\"";
            output.Append("terrabase_requests_total{").Append(labels).Append("} ").Append(series.Total).Append('\n');
            for (var index = 0; index < PrometheusMetrics.RequestLatencyBuckets.Length; index++)
            {
                output.Append("terrabase_request_duration_seconds_bucket{")
                    .Append(labels).Append(",le=\"")
                    .Append(PrometheusMetrics.RequestLatencyBuckets[index].ToString("G", CultureInfo.InvariantCulture))
                    .Append("\"} ").Append(series.Duration.CumulativeBuckets[index]).Append('\n');
            }

            output.Append("terrabase_request_duration_seconds_bucket{").Append(labels).Append(",le=\"+Inf\"} ")
                .Append(series.Duration.Count).Append('\n');
            output.Append("terrabase_request_duration_seconds_count{").Append(labels).Append("} ")
                .Append(series.Duration.Count).Append('\n');
            output.Append("terrabase_request_duration_seconds_sum{").Append(labels).Append("} ")
                .Append(series.Duration.SumSeconds.ToString("G", CultureInfo.InvariantCulture)).Append('\n');
        }

        Help(output, "terrabase_component_health", "Current bounded component health.", "gauge");
        foreach (var check in Health.Checks)
        {
            output.Append("terrabase_component_health{component=\"").Append(Label(check.Component)).Append("\"} ")
                .Append(HealthGauge(check.Status).ToString("G", CultureInfo.InvariantCulture)).Append('\n');
        }

        if (Encoding.UTF8.GetByteCount(output.ToString()) > PrometheusMetrics.MaxBodyBytes)
        {
            throw new ExpositionTooLargeException(PrometheusMetrics.MaxBodyBytes);
        }

        return output.ToString();
    }

    private static void Help(StringBuilder output, string name, string help, string type)
    {
        output.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        output.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static double HealthGauge(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 1,
        HealthStatus.Degraded => 0.5,
        HealthStatus.Unhealthy => 0,
        _ => -1,
    };

    private static string Label<T>(T value) where T : struct, Enum => value switch
    {
        Region region => region switch { Region.Gru => "GRU", Region.Sfo => "SFO", Region.Fra => "FRA", _ => "unknown" },
        Operation operation => operation switch
        {
            Operation.Read => "read",
            Operation.Write => "write",
            Operation.Query => "query",
            Operation.Delete => "delete",
            Operation.Subscribe => "subscribe",
            Operation.Backup => "backup",
            Operation.Restore => "restore",
            Operation.Repair => "repair",
            _ => "other",
        },
        Outcome outcome => outcome switch
        {
            Outcome.Success => "success",
            Outcome.Error => "error",
            Outcome.Rejected => "rejected",
            Outcome.Timeout => "timeout",
            Outcome.Conflict => "conflict",
            _ => "not_found",
        },
        Component component => component switch
        {
            Component.Storage => "storage",
            Component.Replication => "replication",
            Component.Membership => "membership",
            Component.ControlPlane => "control_plane",
            Component.Backup => "backup",
            _ => "metrics",
        },
        _ => value.ToString()!.ToLowerInvariant(),
    };
}

/// <summary>Raised when a Prometheus body exceeds its response bound.</summary>
public sealed class ExpositionTooLargeException : Exception
{
    /// <summary>Creates a size-bound exception.</summary>
    /// <param name="limit">The maximum accepted body size in bytes.</param>
    public ExpositionTooLargeException(int limit)
        : base($"The Prometheus exposition exceeds the {limit} byte limit.")
    {
        Limit = limit;
    }

    /// <summary>The maximum accepted body size.</summary>
    public int Limit { get; }
}
