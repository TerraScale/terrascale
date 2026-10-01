using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeBlockingDisposalTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BlockingSynchronousDisposalCannotHoldCancellationOrDeadline(bool discovery, bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        using var body = new BlockingStream(() => { if (cancelCaller) caller.Cancel(); });
        var calls = 0;
        using var handler = new NativeClientTests.Handler((_, _) =>
        {
            calls++;
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var scope = NativeCodecTests.Scope with { Shard = 1 };
        const string key = "tbk1.AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        try
        {
            if (discovery)
            {
                using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(2), handler);
                var pending = router.ResolveAsync(scope, key, ["read"], caller.Token).AsTask();
                if (cancelCaller)
                    await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                else
                {
                    var failure = await Assert.ThrowsAsync<NativeAdmissionException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                    await Assert.That(failure!.Code).IsEqualTo("discovery_unavailable");
                }
            }
            else
            {
                using var client = new NativeClient(scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
                    _ => ValueTask.FromResult(key), TimeSpan.FromSeconds(2), handler);
                var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
                var result = await client.CommitAsync(prepared, caller.Token).WaitAsync(TimeSpan.FromSeconds(10));
                var error = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
                await Assert.That(error.Code).IsEqualTo(cancelCaller ? "cancelled" : "deadline_exceeded");
                await Assert.That(error.Status).IsEqualTo(200);
                await Assert.That(error.Transaction).IsEqualTo(prepared.Identity);
                await Assert.That(error.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
            }
            await Assert.That(body.DisposalStarted.Task.IsCompleted).IsFalse();
            body.ReadCompletion.TrySetResult(0);
            await body.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(body.Disposed.Task.IsCompleted).IsFalse();
            await Assert.That(calls).IsEqualTo(1);
        }
        finally
        {
            body.ReleaseDisposal.Set();
            body.ReadCompletion.TrySetResult(0);
            await body.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class BlockingStream(Action readStarted) : Stream
    {
        internal TaskCompletionSource<int> ReadCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim ReleaseDisposal { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            readStarted();
            return new ValueTask<int>(ReadCompletion.Task);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposalStarted.TrySetResult();
                ReleaseDisposal.Wait(TimeSpan.FromSeconds(15));
                Disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
