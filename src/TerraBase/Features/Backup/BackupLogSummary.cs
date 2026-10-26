namespace TerraBase.Features.Backup;

internal readonly record struct BackupLogSummary(long Bytes, long Creates, bool Boundary, long? EndMark);

/// <summary>
/// Measures the ChangeLog wire grammar while decoding, without retaining strings or row bodies. This is admission
/// metadata only: engine replay still validates SQL, transaction boundaries, clocks and the exact restore cut.
/// </summary>
internal sealed class BackupLogSummaryStream(DateTimeOffset at) : BackupCountingStream
{
    private enum Part { Kind, Length, Skip, Time }

    private readonly long _milliseconds = at.ToUnixTimeMilliseconds();
    // A commit in the requested second either reaches the target or makes CutLog report an ambiguous time.
    private readonly long _seconds = at.ToUnixTimeSeconds();
    private Part _part;
    private int _kind;
    private int _field;
    private int _shift;
    private int _timeBytes;
    private long _length;
    private long _skip;
    private ulong _clock;
    private long _creates;
    private bool _boundary;
    private long? _endMark;

    public BackupLogSummary Complete() => _part == Part.Kind
        ? new(Length, _creates, _boundary, _endMark)
        : throw new InvalidDataException("a change log record is truncated");

    public override void SetLength(long value)
    {
        base.SetLength(value);
        (_part, _kind, _field, _shift, _timeBytes, _length, _skip, _clock, _creates, _boundary, _endMark) =
            (Part.Kind, 0, 0, 0, 0, 0, 0, 0, 0, false, null);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Consume(buffer.AsSpan(offset, count));
        base.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Consume(buffer.Span);
        return base.WriteAsync(buffer, cancellationToken);
    }

    private void Consume(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_part == Part.Skip)
            {
                var count = (int)Math.Min(_skip, bytes.Length);
                bytes = bytes[count..];
                _skip -= count;
                if (_skip == 0)
                {
                    NextField();
                }

                continue;
            }

            var value = bytes[0];
            bytes = bytes[1..];
            switch (_part)
            {
                case Part.Kind:
                    _kind = value;
                    _field = 0;
                    _endMark = null;
                    if (_kind is < 1 or > 7)
                    {
                        throw new InvalidDataException("a change log record has an unknown kind");
                    }

                    // Untimed commits cannot prove a candidate: replay reports its precise unproven-history error.
                    _boundary |= _kind == 3;
                    NextField();
                    break;
                case Part.Length:
                    _length |= (long)(value & 0x7f) << _shift;
                    _shift += 7;
                    if ((value & 0x80) != 0)
                    {
                        if (_shift >= 35)
                        {
                            throw new InvalidDataException("a change log length is malformed");
                        }
                    }
                    else
                    {
                        _skip = _length <= 1 << 30 ? _length : throw new InvalidDataException("a change log length is out of range");
                        _part = Part.Skip;
                        if (_skip == 0)
                        {
                            NextField();
                        }
                    }

                    break;
                case Part.Time:
                    _clock = (_clock << 8) | value;
                    if (++_timeBytes == 8)
                    {
                        var clock = unchecked((long)_clock);
                        _boundary |= clock >= (_kind == 7 ? _milliseconds : _seconds);
                        _endMark = _kind == 7 ? clock : null;
                        NextField();
                    }

                    break;
                default:
                    throw new InvalidDataException("a change log summary has an invalid state");
            }
        }
    }

    private void NextField()
    {
        // ChangeLog: 1=name,rowid,row; 2=name,rowid; 3=commit; 4=SQL; 5=type,name; 6=first,last; 7=mark.
        _part = (_kind, _field++) switch
        {
            (1, 0) or (2, 0) or (4, 0) or (5, 0) or (1, 2) or (5, 1) => Part.Length,
            (1, 1) or (2, 1) or (6, 0) => Part.Skip,
            (6, 1) or (7, 0) => Part.Time,
            _ => Part.Kind,
        };
        (_length, _shift, _clock, _timeBytes, _skip) = (0, 0, 0, 0, _part == Part.Skip ? 8 : 0);
        if (_part == Part.Kind && _kind == 4)
        {
            _creates = checked(_creates + 1);
        }
    }
}
