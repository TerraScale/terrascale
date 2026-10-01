using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeStreamingTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task NoncooperativeTransportsStayFiniteAndReleaseLateResources(bool bodyRead, bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingBody = new NoncooperativeBodyStream(() => { if (cancelCaller) caller.Cancel(); });
        var calls = 0;
        using var handler = new NativeClientTests.Handler((_, _) =>
        {
            calls++;
            if (!bodyRead)
            {
                if (cancelCaller) caller.Cancel();
                return pendingResponse.Task;
            }
            var content = new StreamContent(pendingBody);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
            _ => ValueTask.FromResult("tbk1.fixture-key"), TimeSpan.FromSeconds(2), handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        try
        {
            var result = await client.CommitAsync(prepared, caller.Token).WaitAsync(TimeSpan.FromSeconds(10));
            var error = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
            await Assert.That(error.Code).IsEqualTo(cancelCaller ? "cancelled" : "deadline_exceeded");
            await Assert.That(error.Transaction).IsEqualTo(prepared.Identity);
            await Assert.That(error.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
            await Assert.That(calls).IsEqualTo(1);
            if (bodyRead)
            {
                await Assert.That(error.Status).IsEqualTo(200);
                await Assert.That(pendingBody.Disposed.Task.IsCompleted).IsTrue();
                await Assert.That(pendingBody.Reads).IsEqualTo(1);
                await Assert.That(pendingBody.AsyncDisposals).IsEqualTo(0);
                pendingBody.Completion.TrySetResult(0);
            }
            else
            {
                await Assert.That(error.Status).IsNull();
                using var lateBody = new DisposalStream();
                pendingResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(lateBody) });
                await lateBody.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            pendingResponse.TrySetResult(NativeClientTests.Json("{}"));
            pendingBody.Completion.TrySetResult(0);
            pendingBody.Dispose();
        }
    }

    [Test]
    public async Task CancellationOnEofCannotPublishAValidCommitReceipt()
    {
        using var caller = new CancellationTokenSource();
        NativeTransactionIdentity? identity = null;
        EofCancellingStream? original = null;
        var calls = 0;
        using var handler = new NativeClientTests.Handler((_, _) =>
        {
            calls++;
            original = new EofCancellingStream(System.Text.Encoding.UTF8.GetBytes(NativeClientTests.Receipt(identity!)), caller.Cancel);
            var content = new StreamContent(original);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = NativeClientTests.Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        identity = prepared.Identity;
        var result = await client.CommitAsync(prepared, caller.Token);
        var error = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
        await Assert.That(error.Code).IsEqualTo("cancelled");
        await Assert.That(error.Status).IsEqualTo(200);
        await Assert.That(error.Transaction).IsEqualTo(prepared.Identity);
        await Assert.That(error.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        await Assert.That(original!.Disposed).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("declared")]
    [Arguments("overflow")]
    [Arguments("cancel")]
    [Arguments("deadline")]
    public async Task StreamedCommitFailuresPreserveUncertainIdentityWithoutReplay(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var body = new BodyStream(mode == "overflow" ? NativeCodec.MaximumBodyBytes + 1 : -1,
            mode == "cancel" ? cancellation.Cancel : null);
        var calls = 0;
        string? sentBody = null;
        using var handler = new NativeClientTests.Handler(async (request, token) =>
        {
            calls++;
            sentBody = await request.Content!.ReadAsStringAsync(token);
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (mode == "declared") content.Headers.ContentLength = NativeCodec.MaximumBodyBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
            _ => ValueTask.FromResult("tbk1.fixture-key"), TimeSpan.FromSeconds(2), handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        var result = await client.CommitAsync(prepared, cancellation.Token);
        var error = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
        await Assert.That(error.Code).IsEqualTo(mode is "declared" or "overflow" ? "invalid_response"
            : mode == "cancel" ? "cancelled" : "deadline_exceeded");
        await Assert.That(error.Status).IsEqualTo(200);
        await Assert.That(error.Transaction).IsEqualTo(prepared.Identity);
        await Assert.That(error.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        await Assert.That(sentBody).IsEqualTo(prepared.RequestBody);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(body.Disposed).IsTrue();
        if (mode == "declared") await Assert.That(body.Reads).IsEqualTo(0);
        if (mode == "overflow") await Assert.That(body.BytesRead).IsEqualTo(NativeCodec.MaximumBodyBytes + 1);
        if (mode is "cancel" or "deadline")
        {
            await Assert.That(body.Reads).IsEqualTo(1);
            await Assert.That(body.CancellationObserved).IsTrue();
        }
    }

    private sealed class DisposalStream() : MemoryStream("{}"u8.ToArray())
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing) { if (disposing) Disposed.TrySetResult(); base.Dispose(disposing); }
    }

    private sealed class NoncooperativeBodyStream(Action readStarted) : Stream
    {
        public TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }
        public int AsyncDisposals { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            readStarted();
            return new ValueTask<int>(Completion.Task);
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed.TrySetResult(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { AsyncDisposals++; return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan)); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class EofCancellingStream(byte[] bytes, Action cancel) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Read(buffer.Span);
            if (count == 0) cancel();
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BodyStream(int remaining, Action? cancel) : Stream
    {
        private CancellationToken readToken;
        public int Reads { get; private set; }
        public int BytesRead { get; private set; }
        public bool CancellationObserved => readToken.IsCancellationRequested;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            readToken = cancellationToken;
            if (remaining >= 0)
            {
                var count = Math.Min(remaining, buffer.Length);
                buffer.Span[..count].Fill((byte)'a');
                remaining -= count;
                BytesRead += count;
                return count;
            }
            cancel?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
