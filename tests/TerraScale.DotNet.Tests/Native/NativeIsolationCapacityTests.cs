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
    public async Task BlockingExtensionCancellationCannotDelayCallerOrDeadline(bool callerCancellation)
    {
        using var blocker = new Blocker(1);
        using var caller = new CancellationTokenSource();
        using var clock = new ArmedDeadlineClock();
        using var stopWatchdog = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdogReleased = false;
        var watchdog = new Thread(() =>
        {
            if (!stopWatchdog.Wait(TimeSpan.FromSeconds(15))) { watchdogReleased = true; blocker.Release.Set(); }
        }) { IsBackground = true };
        watchdog.Start();
        using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("Cancelled provider must never dispatch."));
        using var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
            async token =>
            {
                await Task.Yield();
                entered.TrySetResult();
                await allowRegistration.Task;
                using var registration = token.Register(blocker.Wait);
                registered.TrySetResult();
                await finishProvider.Task;
                return "tbk1.fixture-key";
            }, TimeSpan.FromSeconds(30), handler, clock);
        try
        {
            var request = client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document, caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // ReadAsync has returned with its deadline wait installed; only now
            // let the suspended provider register its newer blocking callback.
            allowRegistration.TrySetResult();
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var beginning = System.Diagnostics.Stopwatch.GetTimestamp();
            if (callerCancellation) caller.Cancel();
            else clock.Arm(TimeSpan.FromMilliseconds(100));
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(System.Diagnostics.Stopwatch.GetElapsedTime(beginning)).IsLessThan(TimeSpan.FromSeconds(5));
            await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code)
                .IsEqualTo(callerCancellation ? "cancelled" : "deadline_exceeded");
            if (!await blocker.Started.WaitAsync(TimeSpan.FromSeconds(5))) throw new TimeoutException("Extension cancellation did not start.");
            await Assert.That(watchdogReleased).IsFalse();
        }
        finally
        {
            allowRegistration.TrySetResult();
            blocker.Release.Set();
            finishProvider.TrySetResult();
            stopWatchdog.Set();
            if (!watchdog.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test watchdog did not finish.");
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.WaitForIdleAsync(drain.Token);
        }
    }

    [Test]
    public async Task CancelledAsyncBodyCopiesFinishBeforeDisposalUsesContinuationWorkers()
    {
        const int capacity = NativeTransport.MaximumOutstandingOperations;
        using var caller = new CancellationTokenSource();
        using var started = new SemaphoreSlim(0);
        using var stopWatchdog = new ManualResetEventSlim();
        var io = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contents = new System.Collections.Concurrent.ConcurrentBag<AsyncReadDisposalContent>();
        var dispatches = 0;
        var watchdogReleased = false;
        var watchdog = new Thread(() =>
        {
            if (!stopWatchdog.Wait(TimeSpan.FromSeconds(15)))
            {
                watchdogReleased = true;
                io.TrySetResult();
                foreach (var content in contents) content.ReleaseDisposal();
            }
        }) { IsBackground = true };
        watchdog.Start();
        using var handler = new NativeClientTests.Handler((_, _) =>
        {
            if (Interlocked.Increment(ref dispatches) > capacity)
                return Task.FromResult(NativeClientTests.Json("{\"version\":1,\"state\":\"absent\",\"state_version\":null,\"context\":[],\"siblings\":[]}"));
            var content = new AsyncReadDisposalContent(io.Task, started);
            contents.Add(content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
            _ => ValueTask.FromResult("tbk1.fixture-key"), TimeSpan.FromSeconds(30), handler);
        try
        {
            var requests = Enumerable.Range(0, capacity).Select(_ =>
                client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document, caller.Token)).ToArray();
            for (var index = 0; index < capacity; index++)
                if (!await started.WaitAsync(TimeSpan.FromSeconds(5))) throw new TimeoutException("Asynchronous body copy did not start.");
            caller.Cancel();
            foreach (var result in await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5)))
                await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("cancelled");
            io.TrySetResult();
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.WaitForIdleAsync(drain.Token);
            var recovered = await client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(recovered is NativeResult<NativePoint>.Success).IsTrue();
            await Assert.That(contents.All(content => content.DisposedAfterRead)).IsTrue();
            await Assert.That(watchdogReleased).IsFalse();
        }
        finally
        {
            io.TrySetResult();
            foreach (var content in contents) content.ReleaseDisposal();
            stopWatchdog.Set();
            if (!watchdog.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test watchdog did not finish.");
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.WaitForIdleAsync(drain.Token);
        }
    }

    [Test]
    public async Task SuspendedCallbacksRetainInvocationContextAndPropagateFaults()
    {
        using var executor = new NativeInvocationExecutor(1);
        var ambient = new AsyncLocal<string>();
        foreach (var value in new[] { "first caller", "second caller" })
        {
            ambient.Value = value;
            var result = await executor.ExecuteAsync(async () =>
            {
                await Task.Yield();
                await Task.Yield();
                return (ambient.Value, Thread.CurrentThread.IsThreadPoolThread);
            });
            await Assert.That(result.Value).IsEqualTo(value);
            await Assert.That(result.IsThreadPoolThread).IsFalse();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync<bool>(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Suspended provider failure.");
        }));
    }

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
                if (!await blocker.Started.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("Capacity callback did not start.");
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
    [Arguments(false)]
    [Arguments(true)]
    public Task ConcurrentClientDeadlinesDoNotDependOnWorkersBlockedByProviders(bool suspend) =>
        ConcurrentClientDeadlinesAsync(suspend, streamRead: false);

    [Test]
    public Task ConcurrentClientDeadlinesDoNotDependOnBlockingReadsAfterSuspension() =>
        ConcurrentClientDeadlinesAsync(suspend: false, streamRead: true);

    private static async Task ConcurrentClientDeadlinesAsync(bool suspend, bool streamRead)
    {
        ThreadPool.GetMinThreads(out var minimum, out _);
        var instances = Math.Clamp((minimum + 31) / 16, 4, 16);
        using var release = new ManualResetEventSlim();
        using var started = new SemaphoreSlim(0);
        using var firstReads = new SemaphoreSlim(0);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopWatchdog = new ManualResetEventSlim();
        using var clock = new ArmedDeadlineClock();
        var clients = new List<NativeClient>();
        var requests = new List<Task<NativeResult<NativePoint>>>();
        var watchdogReleased = false;
        var watchdog = new Thread(() =>
        {
            // The test's own failure guard must survive thread-pool starvation.
            if (!stopWatchdog.Wait(TimeSpan.FromSeconds(15)))
            {
                watchdogReleased = true;
                releaseFirst.TrySetResult();
                release.Set();
            }
        }) { IsBackground = true };
        watchdog.Start();
        try
        {
            for (var index = 0; index < instances; index++)
            {
                var handler = new NativeClientTests.Handler((_, _) =>
                {
                    if (!streamRead) throw new InvalidOperationException("Cancelled providers must never dispatch.");
                    var content = new StreamContent(new SuspendThenBlockStream(firstReads, releaseFirst.Task, started, release));
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                });
                var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
                    async _ =>
                    {
                        if (suspend) await Task.Yield();
                        if (!streamRead)
                        {
                            started.Release();
                            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Provider was not released.");
                        }
                        return "tbk1.fixture-key";
                    }, TimeSpan.FromSeconds(30), handler, clock);
                clients.Add(client);
                for (var operation = 0; operation < NativeTransport.MaximumOutstandingOperations; operation++)
                    requests.Add(client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document));
            }
            if (streamRead)
            {
                // Every first read must actually suspend before returning a
                // positive count and allowing the framework/SDK's next read.
                for (var index = 0; index < requests.Count; index++)
                    if (!await firstReads.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("First stream read did not start.");
                releaseFirst.TrySetResult();
            }
            for (var index = 0; index < requests.Count; index++)
                if (!await started.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent callback did not start.");
            var beginning = System.Diagnostics.Stopwatch.GetTimestamp();
            // These are real shared-pool deadline timers, armed only after all
            // blocking callbacks start. No ThreadPool minimum or maximum is changed.
            clock.Arm(TimeSpan.FromMilliseconds(100));
            var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(System.Diagnostics.Stopwatch.GetElapsedTime(beginning)).IsLessThan(TimeSpan.FromSeconds(5));
            await Assert.That(watchdogReleased).IsFalse();
            foreach (var result in results)
                await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("deadline_exceeded");
            // Disposing while callbacks are suspended/blocked must retain their
            // owned executor until all late continuations and cleanup finish.
            foreach (var client in clients) client.Dispose();
        }
        finally
        {
            releaseFirst.TrySetResult();
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

    private sealed class SuspendThenBlockStream(SemaphoreSlim firstReads, Task releaseFirst,
        SemaphoreSlim started, ManualResetEventSlim release) : Stream
    {
        private int reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                var pending = FirstReadAsync(buffer);
                if (pending.IsCompleted) throw new InvalidOperationException("The first read must suspend.");
                firstReads.Release();
                return pending;
            }
            started.Release();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Second stream read was not released.");
            return ValueTask.FromResult(0);
        }
        private async ValueTask<int> FirstReadAsync(Memory<byte> buffer)
        {
            await releaseFirst;
            buffer.Span[0] = (byte)' ';
            return 1;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private sealed class AsyncReadDisposalContent : HttpContent
    {
        private readonly Task io;
        private readonly SemaphoreSlim started;
        private readonly ManualResetEventSlim readFinished = new();
        private bool readCompleted;
        internal AsyncReadDisposalContent(Task io, SemaphoreSlim started)
        {
            this.io = io;
            this.started = started;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        internal bool DisposedAfterRead { get; private set; }
        internal void ReleaseDisposal() => readFinished.Set();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            try
            {
                started.Release();
                await io;
                await stream.WriteAsync("{\"version\":1,\"state\":\"absent\",\"state_version\":null,\"context\":[],\"siblings\":[]}"u8.ToArray());
            }
            finally { readCompleted = true; readFinished.Set(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!readFinished.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Active body copy did not finish.");
                DisposedAfterRead = readCompleted;
            }
            base.Dispose(disposing);
        }
    }
}
