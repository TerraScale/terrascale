using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Backup;

/// <summary>
/// Batch 8 (2026-10-25): after a roll, a writer held idle for 23.5 s made every catalog anchor time out
/// (<see cref="CatalogGate.AnchorTimeout"/>), and ts-auth saw TerraBase unavailable in all three regions. Here two regions
/// keep writing and anchoring their catalogs while the third restarts twice, as a rolling deploy does: every anchor, the
/// restarted region's included, is written within its deadline.
/// </summary>
public sealed class CatalogAnchorRollingRestartTests
{
    // The anchor's own deadline: an anchor past it is answered catalog_anchor_unavailable, which ts-auth sees as Unavailable.
    private static readonly TimeSpan Within = CatalogGate.AnchorTimeout;
    private static readonly TimeSpan GateOpens = TimeSpan.FromMinutes(3);

    [Test]
    [AnchorDeadlineQuarantine]
    public async Task Anchors_stay_within_their_deadline_while_a_region_restarts_under_catalog_writes()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        var phases = new ConcurrentQueue<(Meter Meter, string Phase, double Seconds)>();
        // Every writer hold of a second or more, by holder and call site: what an anchor that missed its deadline queued behind.
        var holds = new ConcurrentQueue<(Meter Meter, DateTimeOffset Released, string Holder, string At, double Seconds)>();
        var meters = new Dictionary<Meter, string>();
        using var timings = new MeterListener();
        timings.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name is "terrabase.replication.snapshot_import" or "terrabase.storage.writer_held")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        timings.SetMeasurementEventCallback<double>((instrument, seconds, tags, _) =>
        {
            if (instrument.Name == "terrabase.storage.writer_held")
            {
                string? holder = null, at = null;
                foreach (var tag in tags)
                {
                    holder = tag.Key == "holder" ? tag.Value as string : holder;
                    at = tag.Key == "at" ? tag.Value as string : at;
                }

                if (seconds >= 1 && holds.Count < 4096)
                {
                    holds.Enqueue((instrument.Meter, DateTimeOffset.UtcNow, holder ?? "?", at ?? "?", seconds));
                }

                return;
            }

            foreach (var tag in tags)
            {
                if (tag.Key == "phase" && phases.Count < 4096)
                {
                    phases.Enqueue((instrument.Meter, (string)tag.Value!, seconds));
                }
            }
        });
        timings.Start();
        // The engine's own warnings by region: a writer waited on names what it queued behind (its holders' call sites).
        var warnings = new ConcurrentQueue<string>();
        Action<IServiceCollection> Capture(string region)
        {
            return services => TerraBaseTestServer.CaptureDebugLogs(services, new EngineWarnings(region, warnings));
        }
        await using var gru = new RestartingMachine("gru", bucket, members);
        await using var sjc = new RestartingMachine("sjc", bucket, members);
        await using var fra = new RestartingMachine("fra", bucket, members);
        foreach (var (machine, region) in ((RestartingMachine, string)[])[(gru, "gru"), (sjc, "sjc"), (fra, "fra")])
        {
            await machine.StartAsync(Capture(region));
            Track(machine);
        }

        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(machine.Gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
        }

        var anchors = new ConcurrentQueue<(string Region, DateTimeOffset Started, TimeSpan Took, bool Held)>();
        var writes = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);

        async Task WriteAsync(RestartingMachine machine)
        {
            while (!stop.IsCancellationRequested)
            {
                _ = await machine.Api.CreateOrganizationAsync("anchor load " + EntityIds.New());
                Interlocked.Increment(ref writes);
            }
        }

        async Task AnchorAsync(RestartingMachine machine, string region, int count)
        {
            for (var i = 0; i < count && !stop.IsCancellationRequested; i++)
            {
                var at = DateTimeOffset.UtcNow;
                var started = Stopwatch.GetTimestamp();
                var held = await machine.Gate.TryAnchorAsync(token);
                anchors.Enqueue((region, at, Stopwatch.GetElapsedTime(started), held));
            }
        }

        var load = Task.WhenAll(Task.Run(() => WriteAsync(gru), CancellationToken.None), Task.Run(() => WriteAsync(sjc), CancellationToken.None));
        try
        {
            for (var restart = 0; restart < 2; restart++)
            {
                var peers = Task.WhenAll(AnchorAsync(gru, "gru", 50), AnchorAsync(sjc, "sjc", 50));
                await fra.StopAsync();
                // fra misses catalog changes and the peers' new anchors while it is down.
                var missed = Volatile.Read(ref writes) + 50;
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Volatile.Read(ref writes) >= missed), timeout: TimeSpan.FromSeconds(60));
                await fra.StartAsync(Capture("fra"));
                Track(fra);
                // The peers anchored markers fra missed: its gate opens once a revocation snapshot or their streams cover them,
                // which a loaded runner takes longer than a minute for. Only the anchors are bounded here.
                try
                {
                    await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(fra.Gate.IsOpen), timeout: GateOpens);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Assert.Fail($"restart {restart}: fra's catalog gate stayed closed, waiting for: {string.Join("; ", fra.Gate.Waiting)}");
                }
                await Task.WhenAll(peers, AnchorAsync(fra, "fra", 50), WriteCountAsync(fra, 50));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await load;
            foreach (var phase in phases.Where(phase => meters.ContainsKey(phase.Meter)))
            {
                TestContext.Current!.OutputWriter.WriteLine($"SnapshotPhase region={meters[phase.Meter]} phase={phase.Phase} seconds={phase.Seconds:F6}");
            }

            foreach (var hold in holds.Where(hold => meters.ContainsKey(hold.Meter)))
            {
                TestContext.Current!.OutputWriter.WriteLine($"WriterHold region={meters[hold.Meter]} released={hold.Released:HH:mm:ss.fff} holder={hold.Holder} at={hold.At} seconds={hold.Seconds:F3}");
            }

            foreach (var warning in warnings.Where(warning => warning.Contains("writer", StringComparison.Ordinal) || warning.Contains("anchor", StringComparison.Ordinal)))
            {
                TestContext.Current!.OutputWriter.WriteLine(warning);
            }

            foreach (var (region, started, took, held) in anchors.Where(anchor => !anchor.Held || anchor.Took >= Within))
            {
                TestContext.Current!.OutputWriter.WriteLine($"LateAnchor region={region} started={started:HH:mm:ss.fff} seconds={took.TotalSeconds:F3} held={held}");
            }
        }

        var late = anchors.Where(anchor => !anchor.Held || anchor.Took >= Within).Select(anchor => $"{anchor.Region} {anchor.Took.TotalSeconds:F1} s {anchor.Held}").ToList();
        await Assert.That(late).IsEmpty();
        await Assert.That(anchors.Count).IsEqualTo(300);

        void Track(RestartingMachine machine)
        {
            meters[machine.Api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName)] =
                machine.Api.Server.Services.GetRequiredService<TerraBase.Configuration.TerraBaseOptions>().Region;
        }

        async Task WriteCountAsync(RestartingMachine machine, int count)
        {
            for (var i = 0; i < count; i++)
            {
                _ = await machine.Api.CreateOrganizationAsync("restarted load " + EntityIds.New());
            }
        }
    }

    private sealed class EngineWarnings(string region, ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(region, categoryName, lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string region, string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && category.StartsWith("TerraBase.", StringComparison.Ordinal);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel) && lines.Count < 4096)
                {
                    lines.Enqueue($"EngineWarning region={region} at={DateTimeOffset.UtcNow:HH:mm:ss.fff} {category[(category.LastIndexOf('.') + 1)..]}: {formatter(state, null)}");
                }
            }
        }
    }
}

// The CI task permits a named-owner quarantine for at most 24 h. #733: fra's anchors miss 10 s after a restart under the
// parallel suite, with no single long writer hold or wait to blame. Its per-step timing reports the rest when it returns.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AnchorDeadlineQuarantineAttribute()
    : SkipAttribute("#733: fra anchors miss their 10 s deadline after a restart; owner MarioGK / WRK slow-flakes; expires 2026-10-29T21:30:00Z")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(DateTimeOffset.UtcNow < new DateTimeOffset(2026, 10, 29, 21, 30, 0, TimeSpan.Zero));
}
