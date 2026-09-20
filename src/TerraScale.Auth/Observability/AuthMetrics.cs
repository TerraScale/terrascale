using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TerraScale.Auth.Observability;

/// <summary>
/// Bounded process metrics for the private operator scrape surface. Labels are
/// intentionally limited to HTTP method and status class so tenant, instance,
/// database, token, and route values can never become metric cardinality.
/// </summary>
public sealed class AuthMetrics
{
    private static readonly string[] Methods = ["DELETE", "GET", "PATCH", "POST", "PUT", "HEAD", "OPTIONS"];
    private readonly ConcurrentDictionary<(string Method, int StatusClass), RequestTotals> requests = new();
    private readonly ConcurrentDictionary<string, long> events = new(StringComparer.Ordinal);
    private readonly long startedAt = Stopwatch.GetTimestamp();
    private int ready;
    private int liveness = 1;
    private int terrabaseReady;

    public bool IsReady => Volatile.Read(ref ready) == 1;
    public bool IsLive => Volatile.Read(ref liveness) == 1;

    public void SetReady(bool value) => Volatile.Write(ref ready, value ? 1 : 0);

    public void SetLiveness(bool value) => Volatile.Write(ref liveness, value ? 1 : 0);

    public void SetDependencyReady(string dependency, bool value)
    {
        if (dependency != "terrabase")
            throw new ArgumentException("Only the bounded TerraBase dependency label is admitted.", nameof(dependency));
        Volatile.Write(ref terrabaseReady, value ? 1 : 0);
    }

    /// <summary>Records a closed lifecycle outcome without tenant or route labels.</summary>
    public void RecordEvent(string kind, bool success)
    {
        var label = kind switch
        {
            "authentication" or "session" or "webhook" or "storage" => kind,
            _ => "other"
        };
        events.AddOrUpdate($"{label}:{(success ? "success" : "failure")}", 1, static (_, count) => checked(count + 1));
    }

    public void RecordRequest(string method, int statusCode, TimeSpan elapsed)
    {
        var normalizedMethod = Methods.Contains(method, StringComparer.Ordinal) ? method : "OTHER";
        var statusClass = Math.Clamp(statusCode / 100, 1, 5);
        var totals = requests.GetOrAdd((normalizedMethod, statusClass), static _ => new());
        Interlocked.Increment(ref totals.Count);
        Interlocked.Add(ref totals.ElapsedTicks, elapsed.Ticks);
    }

    public string RenderPrometheus()
    {
        var output = new StringBuilder(2048);
        Append(output, "# HELP ts_auth_build_info Build information.");
        Append(output, "# TYPE ts_auth_build_info gauge");
        Append(output, "ts_auth_build_info{service=\"ts-auth\"} 1");
        Append(output, "# HELP ts_auth_process_uptime_seconds Process uptime in seconds.");
        Append(output, "# TYPE ts_auth_process_uptime_seconds gauge");
        Append(output, $"ts_auth_process_uptime_seconds {ElapsedSinceStart().TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)}");
        Append(output, "# HELP ts_auth_ready Whether the HTTP process is serving requests.");
        Append(output, "# TYPE ts_auth_ready gauge");
        Append(output, $"ts_auth_ready {(IsReady ? 1 : 0)}");
        Append(output, "# HELP ts_auth_liveness Whether the process liveness probe is available.");
        Append(output, "# TYPE ts_auth_liveness gauge");
        Append(output, $"ts_auth_liveness {(IsLive ? 1 : 0)}");
        Append(output, "# HELP ts_auth_dependency_ready Whether a required dependency is ready.");
        Append(output, "# TYPE ts_auth_dependency_ready gauge");
        Append(output, $"ts_auth_dependency_ready{{dependency=\"terrabase\"}} {Volatile.Read(ref terrabaseReady)}");
        Append(output, "# HELP ts_auth_events_total Closed lifecycle event outcomes.");
        Append(output, "# TYPE ts_auth_events_total counter");
        foreach (var (key, count) in events.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var separator = key.IndexOf(':');
            var kind = key[..separator];
            var outcome = key[(separator + 1)..];
            Append(output, $"ts_auth_events_total{{kind=\"{kind}\",outcome=\"{outcome}\"}} {count}");
        }
        Append(output, "# HELP ts_auth_http_requests_total HTTP requests by bounded method and status class.");
        Append(output, "# TYPE ts_auth_http_requests_total counter");
        Append(output, "# HELP ts_auth_http_request_duration_seconds_total Cumulative HTTP request duration.");
        Append(output, "# TYPE ts_auth_http_request_duration_seconds_total counter");
        foreach (var ((method, statusClass), totals) in requests.OrderBy(static item => item.Key.Method, StringComparer.Ordinal).ThenBy(static item => item.Key.StatusClass))
        {
            var labels = $"method=\"{method}\",status_class=\"{statusClass}xx\"";
            var count = Volatile.Read(ref totals.Count);
            var duration = TimeSpan.FromTicks(Volatile.Read(ref totals.ElapsedTicks)).TotalSeconds;
            Append(output, $"ts_auth_http_requests_total{{{labels}}} {count.ToString(CultureInfo.InvariantCulture)}");
            Append(output, $"ts_auth_http_request_duration_seconds_total{{{labels}}} {duration.ToString("F6", CultureInfo.InvariantCulture)}");
        }

        return output.ToString();
    }

    private TimeSpan ElapsedSinceStart() => Stopwatch.GetElapsedTime(startedAt);

    private static void Append(StringBuilder output, string line) => output.Append(line).Append('\n');

    private sealed class RequestTotals
    {
        internal long Count;
        internal long ElapsedTicks;
    }
}
