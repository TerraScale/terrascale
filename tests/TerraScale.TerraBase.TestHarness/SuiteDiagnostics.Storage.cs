using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Sqlite;

namespace TerraBase.Testing;

public static partial class SuiteDiagnostics
{
    // Weak references cannot keep a timed-out fixture's servers alive after its owner stops observing.
    private static readonly List<WeakReference<StorageObservation>> StorageObservers = [with(6)];
    private static long _lostStorageObservers;

    internal static StorageObservation ObserveStorage(string scenario, Func<string>? progress = null)
    {
        lock (Samples)
        {
            StorageObservers.RemoveAll(reference => !reference.TryGetTarget(out _));
            var enabled = StorageObservers.Count < 6;
            var observation = new StorageObservation(scenario, progress, enabled);
            if (enabled)
            {
                StorageObservers.Add(new(observation));
            }
            else
            {
                _lostStorageObservers++;
                TestContext.Current!.OutputWriter.WriteLine($"Storage observation skipped: lost={_lostStorageObservers}");
            }

            return observation;
        }
    }

    private static void CaptureStorage(double elapsed, double pause)
    {
        foreach (var reference in StorageObservers)
        {
            if (reference.TryGetTarget(out var observation))
            {
                try
                {
                    observation.Capture(elapsed, pause);
                }
                catch (Exception)
                {
                    // A diagnostic callback must never escape the timer or change the observed workload.
                    observation.CaptureFailed();
                }
            }
        }
    }

    // Await-using cleanup can replace a body's exception at the test deadline. Attempt every owner, print each
    // cleanup error separately, then rethrow the original body (or first cleanup error) with its captured stack.
    internal static async Task CleanupAfterBodyAsync(Exception? bodyFailure, Action finalCleanup, params IAsyncDisposable?[] owners)
    {
        var failure = bodyFailure is null ? null : ExceptionDispatchInfo.Capture(bodyFailure);
        foreach (var owner in owners)
        {
            if (owner is null)
            {
                continue;
            }

            try
            {
                await owner.DisposeAsync();
            }
            catch (Exception error)
            {
                TestContext.Current!.OutputWriter.WriteLine($"Test owner cleanup failed: {error}");
                failure ??= ExceptionDispatchInfo.Capture(error);
            }
        }

        try
        {
            finalCleanup();
        }
        catch (Exception error)
        {
            TestContext.Current!.OutputWriter.WriteLine($"Test directory cleanup failed: {error}");
            failure ??= ExceptionDispatchInfo.Capture(error);
        }

        failure?.Throw();
    }

    internal sealed class StorageObservation : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly string _scenario;
        private readonly Func<string>? _progress;
        private readonly TestContext _context = TestContext.Current!;
        private readonly Queue<StorageSample> _samples = new(Capacity);
        private readonly List<(string Name, SqliteStorageEngine Engine, SqliteDatabaseStore? System,
            SqliteRunnerDiagnostics Io, SqliteRunnerDiagnostics? Writer, double IoStarted, double WriterStarted)> _hosts = [with(4)];
        private string _phase = "prepared";
        private long _phaseStarted = Stopwatch.GetTimestamp();
        private readonly bool _enabled;
        private int _lostHosts;
        private int _captureFailures;
        private bool _headerWritten;
        private bool _failed;
        private bool _disposed;

        internal StorageObservation(string scenario, Func<string>? progress, bool enabled)
        {
            _scenario = scenario[..Math.Min(scenario.Length, 64)];
            _progress = progress;
            _enabled = enabled;
        }

        internal void Phase(string phase)
        {
            lock (_gate)
            {
                _phase = phase[..Math.Min(phase.Length, 64)];
                _phaseStarted = Stopwatch.GetTimestamp();
            }
        }

        internal void ObserveServer(string name, TerraBaseTestServer server)
        {
            if (!_enabled)
            {
                return;
            }

            SqliteStorageEngine engine;
            try
            {
                engine = server.Services.GetRequiredService<SqliteStorageEngine>();
            }
            catch (ObjectDisposedException)
            {
                lock (_gate)
                {
                    _lostHosts++;
                }

                return;
            }

            lock (_gate)
            {
                if (_hosts.Count == 4 || engine.Io.Diagnostics is not null)
                {
                    _lostHosts++;
                    return;
                }

                var io = new SqliteRunnerDiagnostics();
                engine.Io.Diagnostics = io;
                _hosts.Add((name[..Math.Min(name.Length, 16)], engine, null, io, null, Elapsed.TotalSeconds, -1));
            }
        }

        internal void CaptureFailed()
        {
            lock (_gate)
            {
                _captureFailures++;
            }
        }

        internal void BodyFailed(Exception error)
        {
            CaptureCurrent();
            lock (_gate)
            {
                _failed = true;
                _context.OutputWriter.WriteLine($"Storage body failed: scenario={_scenario} phase={_phase} error={error}");
                Report();
            }
        }

        internal void Capture(double elapsed, double pause)
        {
            lock (_gate)
            {
                if (_disposed || !_enabled)
                {
                    return;
                }

                var hosts = new HostSample[_hosts.Count];
                for (var index = 0; index < hosts.Length; index++)
                {
                    var host = _hosts[index];
                    if (host.System is null && host.Engine.SystemStore is { } published)
                    {
                        // The catalog opens lazily. Attach only when its real owner has published the store.
                        host.System = published;
                        if (published.WriteLane.Diagnostics is null)
                        {
                            host.Writer = new();
                            published.WriteLane.Diagnostics = host.Writer;
                            host.WriterStarted = elapsed;
                        }
                        else
                        {
                            _lostHosts++;
                        }

                        _hosts[index] = host;
                    }

                    var engine = host.Engine;
                    var system = host.System;
                    // Timing snapshots release the recorder lock before any runner queue lock is taken.
                    var ioTiming = host.Io.Sample();
                    var writerTiming = host.Writer?.Sample();
                    hosts[index] = new(host.Name, engine.Io.Admitted, engine.Io.Running, engine.Io.Queued,
                        system is null ? -1 : system.WriteLane.Running ? 1 : 0, system?.WriteLane.Queued ?? -1,
                        system?.QueuedWrites ?? -1, ioTiming, writerTiming, host.IoStarted, host.WriterStarted);
                }

                var progress = _progress?.Invoke() ?? "";
                if (_samples.Count == Capacity)
                {
                    _samples.Dequeue();
                }

                _samples.Enqueue(new(elapsed, pause, _phase, Stopwatch.GetElapsedTime(_phaseStarted).TotalSeconds,
                    progress[..Math.Min(progress.Length, 1024)], hosts));
            }
        }

        private void CaptureCurrent()
        {
            try
            {
                Capture(Elapsed.TotalSeconds, GC.GetTotalPauseDuration().TotalSeconds);
            }
            catch (Exception)
            {
                CaptureFailed();
            }
        }

        public void Dispose()
        {
            lock (Samples)
            {
                StorageObservers.RemoveAll(reference => !reference.TryGetTarget(out var observation) || ReferenceEquals(observation, this));
            }

            CaptureCurrent();
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_failed || _context.Execution.CancellationToken.IsCancellationRequested)
                {
                    Report();
                }

                foreach (var host in _hosts)
                {
                    if (ReferenceEquals(host.Engine.Io.Diagnostics, host.Io))
                    {
                        host.Engine.Io.Diagnostics = null;
                    }

                    if (host.System is { } system && host.Writer is not null
                        && ReferenceEquals(system.WriteLane.Diagnostics, host.Writer))
                    {
                        system.WriteLane.Diagnostics = null;
                    }
                }

                _hosts.Clear();
                _samples.Clear();
            }
        }

        private void Report()
        {
            if (!_headerWritten)
            {
                _headerWritten = true;
                _context.OutputWriter.WriteLine($"Storage observation: scenario={_scenario} coverage=shared_io,catalog_writer tenant_write_lanes=unobserved counts=tracked lost=untracked unavailable=-1 catalog_timing=from_first_published_store_sample");
                _context.OutputWriter.WriteLine("Storage phase CSV: elapsed_s,gc_pause_s,phase,phase_age_s,progress");
                _context.OutputWriter.WriteLine("Storage runner CSV: elapsed_s,host,lane,admitted,queued,running,group_queued,submitted,begun,completed,resumed,cancelled,cancelled_resumed,aborted,lost,queue_s,callback_s,resume_s,oldest_queue_s,oldest_callback_s,oldest_resume_s,oldest_cancel_resume_s,timing_started_s");
            }

            _context.OutputWriter.WriteLine($"Storage observation losses: hosts={_lostHosts} samples={_captureFailures}");
            while (_samples.TryDequeue(out var sample))
            {
                _context.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Storage phase: {sample.Elapsed:F3},{sample.Pause:F3},{sample.Phase},{sample.PhaseAge:F3},{sample.Progress}"));
                foreach (var host in sample.Hosts)
                {
                    WriteRunner("io", host.Io, host.IoQueued, host.IoRunning, host.IoStarted);
                    WriteRunner("catalog_writer", host.Writer, host.WriterQueued, host.WriterRunning, host.WriterStarted);

                    void WriteRunner(string lane, SqliteRunnerDiagnostics.Snapshot? timing, int queued, int running, double started)
                    {
                        _context.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"Storage runner: {sample.Elapsed:F3},{host.Name},{lane},{host.Admitted},{queued},{running},{host.GroupQueued},{timing?.Submitted ?? -1},{timing?.Begun ?? -1},{timing?.Completed ?? -1},{timing?.Resumed ?? -1},{timing?.Cancelled ?? -1},{timing?.CancelledResumed ?? -1},{timing?.Aborted ?? -1},{timing?.Lost ?? -1},{Seconds(timing?.QueueTicks):F6},{Seconds(timing?.CallbackTicks):F6},{Seconds(timing?.ResumeTicks):F6},{Seconds(timing?.OldestQueuedTicks):F6},{Seconds(timing?.OldestRunningTicks):F6},{Seconds(timing?.OldestResumeTicks):F6},{Seconds(timing?.OldestCancelledResumeTicks):F6},{started:F3}"));
                    }
                }
            }
        }

        private static double Seconds(long? ticks) => ticks is { } value ? value / (double)Stopwatch.Frequency : -1;

        private readonly record struct StorageSample(double Elapsed, double Pause, string Phase, double PhaseAge, string Progress, HostSample[] Hosts);
        private readonly record struct HostSample(string Name, int Admitted, int IoRunning, int IoQueued, int WriterRunning, int WriterQueued, int GroupQueued,
            SqliteRunnerDiagnostics.Snapshot Io, SqliteRunnerDiagnostics.Snapshot? Writer, double IoStarted, double WriterStarted);
    }
}
