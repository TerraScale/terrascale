using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeStreamingTests
{
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
        public int Reads { get; private set; }
        public int BytesRead { get; private set; }
        public bool CancellationObserved { get; private set; }
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
            if (remaining >= 0)
            {
                var count = Math.Min(remaining, buffer.Length);
                buffer.Span[..count].Fill((byte)'a');
                remaining -= count;
                BytesRead += count;
                return count;
            }
            cancel?.Invoke();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
