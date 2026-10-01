using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeBlockingInvocationTests
{
    [Test]
    [Arguments("credential", false)]
    [Arguments("credential", true)]
    [Arguments("resolve", false)]
    [Arguments("resolve", true)]
    [Arguments("assert", false)]
    [Arguments("assert", true)]
    [Arguments("send", false)]
    [Arguments("send", true)]
    [Arguments("length", false)]
    [Arguments("length", true)]
    [Arguments("serialize", false)]
    [Arguments("serialize", true)]
    [Arguments("read", false)]
    [Arguments("read", true)]
    public async Task SynchronousCallbacksCannotHoldTheNativeDeadline(string stage, bool cancelCaller)
    {
        using var blocker = new Blocker();
        using var caller = new CancellationTokenSource();
        using var content = new BlockingContent(stage, blocker);
        var calls = 0;
        string? retainedBody = null;
        using var handler = new NativeClientTests.Handler(async (request, _) =>
        {
            Interlocked.Increment(ref calls);
            if (stage == "send") blocker.Wait();
            retainedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new NativeClient(NativeCodecTests.Scope, new BlockingProvider(stage, blocker),
            _ =>
            {
                if (stage == "credential") blocker.Wait();
                return ValueTask.FromResult("tbk1.fixture-key");
            }, TimeSpan.FromSeconds(2), handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        // Keep the test itself finite against the unfixed synchronous invocation.
        var pending = Task.Run(() => client.CommitAsync(prepared, caller.Token));
        try
        {
            await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancelCaller) caller.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            var error = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
            await Assert.That(error.Code).IsEqualTo(cancelCaller ? "cancelled" : "deadline_exceeded");
            await Assert.That(error.Transaction).IsEqualTo(prepared.Identity);
            var dispatched = stage is "send" or "length" or "serialize" or "read";
            await Assert.That(error.MutationOutcome).IsEqualTo(dispatched ? NativeMutationOutcome.Uncertain : NativeMutationOutcome.Unknown);
            await Assert.That(calls).IsEqualTo(dispatched ? 1 : 0);
        }
        finally
        {
            blocker.Release.Set();
            await blocker.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (stage is "send" or "length" or "serialize" or "read")
        {
            await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(retainedBody).IsEqualTo(prepared.RequestBody);
        }
        else await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("send", false)]
    [Arguments("send", true)]
    [Arguments("length", false)]
    [Arguments("length", true)]
    [Arguments("serialize", false)]
    [Arguments("serialize", true)]
    [Arguments("read", false)]
    [Arguments("read", true)]
    public async Task SynchronousDiscoveryCallbacksReleaseTheScopeOnCancellation(string stage, bool cancelCaller)
    {
        using var blocker = new Blocker();
        using var caller = new CancellationTokenSource();
        using var content = new BlockingContent(stage, blocker);
        var calls = 0;
        using var handler = new NativeClientTests.Handler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                return Task.FromResult(NativeClientTests.Json("{\"error\":{\"code\":\"discovery_forbidden\"}}", HttpStatusCode.Forbidden));
            if (stage == "send") blocker.Wait();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(2), handler);
        var scope = NativeCodecTests.Scope with { Shard = 1 };
        const string key = "tbk1.AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var pending = Task.Run(async () => await router.ResolveAsync(scope, key, ["read"], caller.Token));
        try
        {
            await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancelCaller) caller.Cancel();
            if (cancelCaller)
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            else
            {
                var failure = await Assert.ThrowsAsync<NativeAdmissionException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                await Assert.That(failure!.Code).IsEqualTo("discovery_unavailable");
            }
            var next = await Assert.ThrowsAsync<NativeAdmissionException>(() =>
                router.ResolveAsync(scope, key, ["read"], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            await Assert.That(next!.Code).IsEqualTo("discovery_forbidden");
            await Assert.That(calls).IsEqualTo(2);
        }
        finally
        {
            blocker.Release.Set();
            await blocker.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await content.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class BlockingProvider(string stage, Blocker blocker) : INativeAdmissionProvider
    {
        public ValueTask<NativeAdmission> ResolveAsync(NativeScope scope, string apiKey, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken)
        {
            if (stage == "resolve") blocker.Wait();
            return ValueTask.FromResult(new NativeAdmission("https://gru.api.terrascale.tech", () =>
            {
                if (stage == "assert") blocker.Wait();
            }));
        }
    }

    private sealed class Blocker : IDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal void Wait()
        {
            Started.TrySetResult();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test callback was not released.");
            }
            finally { Finished.TrySetResult(); }
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class BlockingContent : HttpContent
    {
        private readonly string stage;
        private readonly Blocker blocker;
        private readonly Stream stream;
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BlockingContent(string stage, Blocker blocker)
        {
            this.stage = stage;
            this.blocker = blocker;
            stream = new BlockingReadStream(stage, blocker);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context)
        {
            if (stage == "serialize") blocker.Wait();
            return stream.CopyToAsync(destination);
        }
        protected override bool TryComputeLength(out long length) { if (stage == "length") blocker.Wait(); length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { stream.Dispose(); Disposed.TrySetResult(); }
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingReadStream(string stage, Blocker blocker) : Stream
    {
        private readonly MemoryStream body = new("{}"u8.ToArray());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stage == "read") blocker.Wait();
            return body.ReadAsync(buffer, cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) body.Dispose(); base.Dispose(disposing); }
    }
}
