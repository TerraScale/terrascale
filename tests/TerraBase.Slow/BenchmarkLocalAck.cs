using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace TerraBase.Slow;

// Request dispatch through local durable commit; remote confirmation has not started when Record runs.
internal sealed class BenchmarkLocalAck(IHttpContextAccessor contexts)
{
    private static readonly object Started = new();
    private readonly Lock _gate = new();
    private readonly long[] _bins = new long[600_001];
    private long _count;
    private long _windowStarted = Stopwatch.GetTimestamp();

    public void Record()
    {
        var context = contexts.HttpContext;
        if (context?.Items[Started] is not long started || !context.Items.Remove(Started))
        {
            return;
        }

        var bin = (int)Math.Min(600_000, Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 10));
        // Snapshot/reset and recording share the gate so a concurrent commit cannot fall between windows.
        lock (_gate)
        {
            Interlocked.Increment(ref _bins[bin]);
            _count++;
        }
    }

    public JsonObject Snapshot(string id)
    {
        lock (_gate)
        {
            var snapshot = new JsonObject
            {
                ["id"] = id,
                ["pid"] = Environment.ProcessId,
                ["local_ack_count"] = _count,
                ["local_ack_p50_ms"] = Quantile(0.50),
                ["local_ack_p99_ms"] = Quantile(0.99),
                ["local_ack_capped_count"] = _bins[^1],
                ["window_started_ticks"] = _windowStarted,
                ["timestamp_frequency"] = Stopwatch.Frequency,
            };
            Array.Clear(_bins);
            _count = 0;
            _windowStarted = Stopwatch.GetTimestamp();
            snapshot["reset_at_ticks"] = _windowStarted;
            return snapshot;
        }
    }

    private double? Quantile(double fraction)
    {
        if (_count == 0)
        {
            return null;
        }

        var rank = (long)Math.Ceiling(_count * fraction);
        long seen = 0;
        for (var bin = 0; bin < _bins.Length; bin++)
        {
            seen += _bins[bin];
            if (seen >= rank)
            {
                return bin / 10.0;
            }
        }

        throw new InvalidOperationException("local acknowledgement histogram count is inconsistent");
    }

    internal sealed class DispatchTimer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, proceed) =>
            {
                context.Items[Started] = Stopwatch.GetTimestamp();
                return proceed(context);
            });
            next(app);
        };
    }
}
