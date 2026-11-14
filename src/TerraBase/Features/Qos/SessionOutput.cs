using System.IO.Pipelines;
using System.Net.Sockets;

namespace TerraBase.Features.Qos;

internal sealed class SessionOutput(PipeWriter inner, Socket socket, TimeProvider time) : PipeWriter
{
    public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
    public override long UnflushedBytes => inner.UnflushedBytes;
    public override void Advance(int bytes) => inner.Advance(bytes);
    public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
    public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    public override void CancelPendingFlush() => inner.CancelPendingFlush();
    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = new SessionSendDeadline(socket, time, cancellationToken);
        return await inner.FlushAsync(deadline.Token).ConfigureAwait(false);
    }
}
