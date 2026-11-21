using TerraBase.Errors;

namespace TerraBase.Features.Backup;

internal readonly record struct BackupLogSummary(long Bytes, long Creates, bool Boundary, long? EndMark);

internal readonly union BackupLogSummaryResult(BackupLogSummary, Validation);

internal sealed class BackupLogSummaryStream(DateTimeOffset at) : BackupCountingStream
{
    private enum Part { Kind, Length, Skip, Time }

    private readonly long _milliseconds = at.ToUnixTimeMilliseconds();
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
    private Validation? _failure;

    public BackupLogSummaryResult Complete() => _part == Part.Kind && _failure is null
        ? new BackupLogSummary(Length, _creates, _boundary, _endMark)
        : _failure ?? new Validation("a change log record is truncated");

    public override void SetLength(long value)
    {
        base.SetLength(value);
        (_part, _kind, _field, _shift, _timeBytes, _length, _skip, _clock, _creates, _boundary, _endMark, _failure) =
            (Part.Kind, 0, 0, 0, 0, 0, 0, 0, 0, false, null, null);
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
            if (_failure is not null)
            {
                return;
            }

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
                        _failure = new Validation("a change log record has an unknown kind");
                        return;
                    }

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
                            _failure = new Validation("a change log length is malformed");
                            return;
                        }
                    }
                    else
                    {
                        if (_length > 1 << 30)
                        {
                            _failure = new Validation("a change log length is out of range");
                            return;
                        }

                        _skip = _length;
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
