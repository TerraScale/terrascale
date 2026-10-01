using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

[NotInParallel]
public sealed class NativeIsolationCapacityTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TimedOutCallbacksAndCleanupHoldCapacityUntilActualCompletion(bool cleanup)
    {
        const int capacity = 16;
        using var blocker = new Blocker(capacity);
        using var caller = new CancellationTokenSource();
        var credentialCalls = 0;
        var dispatches = 0;
        using var handler = new NativeClientTests.Handler((_, _) =>
        {
            var count = Interlocked.Increment(ref dispatches);
            if (cleanup && count <= capacity)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingDisposalContent(blocker) });
            return Task.FromResult(NativeClientTests.Json("{\"version\":1,\"state\":\"absent\",\"state_version\":null,\"context\":[],\"siblings\":[]}"));
        });
        using var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
            _ =>
            {
                if (Interlocked.Increment(ref credentialCalls) <= capacity && !cleanup) blocker.Wait();
                return ValueTask.FromResult("tbk1.fixture-key");
            }, TimeSpan.FromSeconds(30), handler);
        var originals = new List<Task<NativeResult<NativePoint>>>();
        try
        {
            for (var index = 0; index < capacity; index++)
            {
                originals.Add(client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document, caller.Token));
                await blocker.Started.WaitAsync(TimeSpan.FromSeconds(10));
            }
            caller.Cancel();
            foreach (var result in await Task.WhenAll(originals).WaitAsync(TimeSpan.FromSeconds(10)))
                await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("cancelled");

            // Caller completion must not free slots still held by callbacks or
            // disposal. New work cancels in the queue without invoking providers.
            using var queued = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var probes = Enumerable.Range(0, 3).Select(_ =>
                client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document, queued.Token)).ToArray();
            foreach (var result in await Task.WhenAll(probes).WaitAsync(TimeSpan.FromSeconds(10)))
                await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("cancelled");
            await Assert.That(credentialCalls).IsEqualTo(capacity);
            await Assert.That(dispatches).IsEqualTo(cleanup ? capacity : 0);
        }
        finally
        {
            caller.Cancel();
            blocker.Release.Set();
            await blocker.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var recovered = await client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(recovered is NativeResult<NativePoint>.Success).IsTrue();
        await Assert.That(credentialCalls).IsEqualTo(capacity + 1);
    }

    [Test]
    public async Task ConcurrentClientDeadlinesDoNotDependOnWorkersBlockedByProviders()
    {
        ThreadPool.GetMinThreads(out var minimum, out _);
        var instances = Math.Clamp((minimum + 31) / 16, 4, 16);
        using var release = new ManualResetEventSlim();
        using var started = new SemaphoreSlim(0);
        using var stopWatchdog = new ManualResetEventSlim();
        using var clock = new ArmedDeadlineClock();
        var clients = new List<NativeClient>();
        var requests = new List<Task<NativeResult<NativePoint>>>();
        var watchdogReleased = false;
        var watchdog = new Thread(() =>
        {
            // The test's own failure guard must survive thread-pool starvation.
            if (!stopWatchdog.Wait(TimeSpan.FromSeconds(15))) { watchdogReleased = true; release.Set(); }
        }) { IsBackground = true };
        watchdog.Start();
        try
        {
            for (var index = 0; index < instances; index++)
            {
                var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("Cancelled providers must never dispatch."));
                var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
                    _ =>
                    {
                        started.Release();
                        if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Provider was not released.");
                        return ValueTask.FromResult("tbk1.fixture-key");
                    }, TimeSpan.FromSeconds(30), handler, clock);
                clients.Add(client);
                for (var operation = 0; operation < NativeTransport.MaximumOutstandingOperations; operation++)
                    requests.Add(client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document));
            }
            for (var index = 0; index < requests.Count; index++)
                if (!await started.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent provider did not start.");
            var beginning = System.Diagnostics.Stopwatch.GetTimestamp();
            // These are real shared-pool deadline timers, armed only after all
            // providers start. No ThreadPool minimum or maximum is changed.
            clock.Arm(TimeSpan.FromMilliseconds(100));
            var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(System.Diagnostics.Stopwatch.GetElapsedTime(beginning)).IsLessThan(TimeSpan.FromSeconds(5));
            await Assert.That(watchdogReleased).IsFalse();
            foreach (var result in results)
                await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("deadline_exceeded");
        }
        finally
        {
            release.Set();
            stopWatchdog.Set();
            if (!watchdog.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test watchdog did not finish.");
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var client in clients)
            {
                try { await client.WaitForIdleAsync(drain.Token); }
                finally { client.Dispose(); }
            }
        }
    }

    private sealed class ArmedDeadlineClock : TimeProvider, IDisposable
    {
        private readonly List<ITimer> timers = [];
        private readonly object gate = new();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            lock (gate) timers.Add(timer);
            return timer;
        }
        internal void Arm(TimeSpan delay) { lock (gate) foreach (var timer in timers) timer.Change(delay, Timeout.InfiniteTimeSpan); }
        public void Dispose() { lock (gate) foreach (var timer in timers) timer.Dispose(); }
    }

    private sealed class Blocker(int capacity) : IDisposable
    {
        private int completed;
        internal SemaphoreSlim Started { get; } = new(0);
        internal ManualResetEventSlim Release { get; } = new();
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Wait()
        {
            Started.Release();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Test callback was not released.");
            }
            finally { if (Interlocked.Increment(ref completed) == capacity) Finished.TrySetResult(); }
        }
        public void Dispose() { Started.Dispose(); Release.Dispose(); }
    }

    private sealed class BlockingDisposalContent : HttpContent
    {
        private readonly Blocker blocker;
        private int disposed;
        internal BlockingDisposalContent(Blocker blocker)
        {
            this.blocker = blocker;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync("{\"version\":1,\"state\":\"absent\",\"state_version\":null,\"context\":[],\"siblings\":[]}"u8.ToArray()).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) blocker.Wait();
            base.Dispose(disposing);
        }
    }
}
