using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TerraBase.Indexing.Runtime;

namespace TerraBase.Benchmarks;

internal static partial class QueryIndependentIndexWorkload
{
    // Captures the diagnostic spans for one bounded query. The first-query
    // phase probe and the separate status-repeat diagnostic use this listener.
    private sealed class FirstQueryActivityTrace : IDisposable
    {
        private readonly ConcurrentDictionary<Activity, long> started = new();
        private readonly ConcurrentQueue<StoppedActivity> stopped = new();
        private readonly object captureGate = new();
        private readonly ActivityListener listener;
        private bool captured;

        public FirstQueryActivityTrace()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == IndexQueryDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity =>
                {
                    var startedAt = Stopwatch.GetTimestamp();
                    lock (captureGate)
                    {
                        if (!captured)
                        {
                            started[activity] = startedAt;
                        }
                    }
                },
                ActivityStopped = activity =>
                {
                    var stoppedAt = Stopwatch.GetTimestamp();
                    lock (captureGate)
                    {
                        // Capture represents any still-running activity at its
                        // snapshot timestamp. A callback racing with capture
                        // must not append a duplicate or extend that interval.
                        if (captured)
                        {
                            return;
                        }

                        var startedAt = started.TryRemove(activity, out var value)
                            ? value
                            : stoppedAt;
                        stopped.Enqueue(new StoppedActivity(activity, startedAt, stoppedAt));
                    }
                },
            };
            ActivitySource.AddActivityListener(listener);
        }

        public FirstQueryTraceSpan[] Complete()
        {
            var activities = Capture();
            var root = activities.FirstOrDefault(static activity => activity.Activity.DisplayName == "automatic_index.query");
            if (root is null)
                throw new InvalidOperationException("The traced query did not emit the root ActivitySource span.");
            return activities
                .Where(activity => activity.Activity.TraceId == root.Activity.TraceId)
                .OrderBy(static activity => activity.StartedAt)
                .Select(static activity => ToSpan(activity, 0, 0))
                .ToArray();
        }

        /// <summary>
        /// Returns every sampled activity, retaining monotonic start/stop
        /// timestamps so background feed spans can be compared with the exact
        /// query window without relying on wall clock time. Activities still
        /// running at capture are conservatively stopped at that timestamp.
        /// </summary>
        public FirstQueryTraceSpan[] CompleteAll(long overlapStart, long overlapStop)
        {
            return Capture()
                .OrderBy(static activity => activity.StartedAt)
                .Select(activity => ToSpan(activity, overlapStart, overlapStop))
                .ToArray();
        }

        private StoppedActivity[] Capture()
        {
            listener.Dispose();
            var capturedAt = Stopwatch.GetTimestamp();
            lock (captureGate)
            {
                captured = true;
                var byActivity = new Dictionary<Activity, StoppedActivity>(ActivityReferenceComparer.Instance);
                foreach (var completed in stopped)
                {
                    byActivity[completed.Activity] = completed;
                }

                foreach (var active in started)
                {
                    byActivity.TryAdd(
                        active.Key,
                        new StoppedActivity(active.Key, active.Value, capturedAt));
                }

                return byActivity.Values.ToArray();
            }
        }

        private static FirstQueryTraceSpan ToSpan(
            StoppedActivity stopped,
            long overlapStart,
            long overlapStop)
        {
            var overlapTicks = overlapStart == 0 || overlapStop <= overlapStart
                ? 0
                : Math.Max(
                    0,
                    Math.Min(stopped.StoppedAt, overlapStop)
                    - Math.Max(stopped.StartedAt, overlapStart));
            var elapsedTicks = stopped.StoppedAt > stopped.StartedAt
                ? stopped.StoppedAt - stopped.StartedAt
                : 0;
            return new FirstQueryTraceSpan(
                stopped.Activity.DisplayName,
                stopped.Activity.SpanId.ToString(),
                stopped.Activity.ParentSpanId.ToString(),
                stopped.Activity.TraceId.ToString(),
                elapsedTicks * 1_000d / Stopwatch.Frequency,
                stopped.StartedAt,
                stopped.StoppedAt,
                overlapTicks * 1_000d / Stopwatch.Frequency,
                stopped.Activity.TagObjects.ToDictionary(
                    static tag => tag.Key,
                    static tag => tag.Value?.ToString() ?? string.Empty,
                    StringComparer.Ordinal));
        }

        public void Dispose() => listener.Dispose();

        private sealed record StoppedActivity(Activity Activity, long StartedAt, long StoppedAt);

        private sealed class ActivityReferenceComparer : IEqualityComparer<Activity>
        {
            internal static ActivityReferenceComparer Instance { get; } = new();

            public bool Equals(Activity? x, Activity? y) => ReferenceEquals(x, y);

            public int GetHashCode(Activity obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    private sealed record FirstQueryTraceSpan(string Name, string SpanId,
        string ParentSpanId, string TraceId, double ElapsedMilliseconds,
        long MonotonicStartTimestamp, long MonotonicStopTimestamp,
        double OverlapMilliseconds,
        IReadOnlyDictionary<string, string> Tags);
}
