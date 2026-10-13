namespace TerraScale.DotNet;

// SseParser has no event bound. Check bytes before the parser can accumulate an unbounded line or data field.
internal sealed class BoundedSseStream(Stream source, CancellationToken cancellationToken) : Stream
{
    internal const int MaxEventBytes = 4 * 1024 * 1024;
    private int eventBytes;
    private int lineBytes;
    private bool afterCarriageReturn;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        read.CancelAfter(TimeSpan.FromSeconds(45)); // Producer heartbeats every 15 seconds; a stalled read has a fixed bound.
        int count;
        try
        {
            count = await source.ReadAsync(buffer, read.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("TerraBase changes stream stopped sending bytes for 45 seconds.", error);
        }
        Check(buffer.Span[..count]);
        return count;
    }

    private void Check(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (afterCarriageReturn && value == (byte)'\n')
            {
                afterCarriageReturn = false;
                continue;
            }
            afterCarriageReturn = value == (byte)'\r';
            if (++eventBytes > MaxEventBytes) throw new InvalidDataException("TerraBase SSE event exceeds 4 MiB.");
            if (value is (byte)'\r' or (byte)'\n')
            {
                if (lineBytes == 0) eventBytes = 0;
                lineBytes = 0;
            }
            else lineBytes++;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
