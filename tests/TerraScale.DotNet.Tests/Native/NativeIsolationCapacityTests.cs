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
