using TerraBase.Features.Memory;

namespace TerraBase.Features.Qos;

// The middleware's 16 KiB reservation covers one 4 KiB read scratch buffer and its copies.
// Charge retained bytes before returning them to the decoder, including fragmented network reads.
internal sealed class QosBodyStream(Stream inner, IMemoryBudget budget) : Stream
{
    private readonly List<IMemoryReservation> _reservations = [];
    private const int ChunkBytes = 4096;
    private const int MaxChunks = 1025;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, ChunkBytes);
        var read = 0;
        while (read < count)
        {
            var received = await inner.ReadAsync(buffer[read..count], cancellationToken).ConfigureAwait(false);
            if (received == 0)
            {
                break;
            }

            read += received;
        }

        if (read == 0)
        {
            return 0;
        }

        if (_reservations.Count == MaxChunks || budget.TryReserve(MemoryLane.Foreground, read * 3L) is not { } reservation)
        {
            throw new QosMemoryException();
        }

        _reservations.Add(reservation);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("request bodies use asynchronous reads");
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var reservation in _reservations)
            {
                reservation.Dispose();
            }

            _reservations.Clear();
        }

        // Kestrel owns the underlying request stream.
        base.Dispose(disposing);
    }

}
