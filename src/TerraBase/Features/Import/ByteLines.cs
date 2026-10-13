namespace TerraBase.Features.Import;

/// <summary>
/// Reads a file line by line as bytes, tracking each line's offset, with one bounded buffer: a line longer than
/// <see cref="MaxLine"/> stops reading. A line ends at <c>\n</c>; a <c>\r</c> before it is dropped.
/// </summary>
internal sealed class ByteLines : IDisposable
{
    public const int MaxLine = 16 * 1024 * 1024;

    private readonly FileStream _stream;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private long _bufferOffset;
    private bool _eof;

    public ByteLines(string path, long offset)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan) { Position = offset };
        _bufferOffset = offset;
    }

    /// <summary>The offset of the next line.</summary>
    public long Position => _bufferOffset + _start;

    /// <summary>The offset of the line the last <see cref="TryRead"/> returned.</summary>
    public long LineStart { get; private set; }

    /// <summary>The next line without its terminator; valid until the next call. False at the end of the file.</summary>
    public bool TryRead(out ReadOnlySpan<byte> line)
    {
        var scanned = 0;
        LineStart = Position;
        while (true)
        {
            var newline = _buffer.AsSpan(_start + scanned, _end - _start - scanned).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                var length = scanned + newline;
                line = Trim(_buffer.AsSpan(_start, length));
                _start += length + 1;
                return true;
            }

            scanned = _end - _start;
            if (scanned > MaxLine)
            {
                throw new SourceFormatException("malformed_source", $"a line at byte {Position} exceeds 16 MiB");
            }

            if (_eof)
            {
                line = Trim(_buffer.AsSpan(_start, _end - _start));
                var any = _end > _start;
                _start = _end;
                return any;
            }

            Fill();
        }
    }

    public void Dispose() => _stream.Dispose();

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line) => line is [.., (byte)'\r'] ? line[..^1] : line;

    private void Fill()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _bufferOffset += _start;
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, MaxLine + 64 * 1024));
        }

        var read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        _eof = read == 0;
        _end += read;
    }
}
