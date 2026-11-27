using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Text.Unicode;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Sessions;

namespace TerraBase.Postgres.Protocol;

internal sealed class PgFrameReader(PipeReader input, TimeProvider timeProvider) : IDisposable
{
    public const int ProtocolVersion3 = 196608;
    private const int CancelCode = 80877102;
    private const int SslCode = 80877103;
    private const int GssCode = 80877104;
    private const int InitialParameterBufferBytes = 256;
    private const int MaxRetainedParameterBytes = 65536;
    private const int InitialParameterSlices = 16;
    private const int MaxRetainedParameterSlices = 256;

    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(30);
    private static readonly short[] FormatZero = [0];
    private static readonly short[] FormatOne = [1];
    private static readonly int[] OidZero = [0];
    private static readonly int[] OidInt8 = [PgOid.Int8];
    private static readonly int[] OidInt4 = [PgOid.Int4];
    private static readonly int[] OidText = [PgOid.Text];
    private static readonly int[] OidZeroZero = [0, 0];
    private static readonly int[] OidInt8Int8 = [PgOid.Int8, PgOid.Int8];

    private readonly Lock _timerGate = new();
    private readonly PgQueryMessage _queryMessage = new();
    private readonly PgParseMessage _parseMessage = new();
    private readonly PgBindMessage _bindMessage = new();
    private readonly PgDescribeMessage _describeMessage = new();
    private readonly PgExecuteMessage _executeMessage = new();
    private readonly PgCloseMessage _closeMessage = new();

    private PostgresSession? _session;
    private long? _interruptedStart;
    private long _interruptedSeen;
    private CancellationTokenSource? _readCts;
    private CancellationTokenSource? _activeReadCts;
    private ITimer? _readTimer;
    private long _readGeneration;
    private long _readDeadline;
    private long _timerScheduledDeadline = long.MaxValue;
    private bool _disposed;
    private CancellationToken _registeredToken;
    private CancellationTokenRegistration _closedRegistration;
    private byte[] _parameterBuffer = new byte[InitialParameterBufferBytes];
    private PgParameterSlice[] _parameterSlices = new PgParameterSlice[InitialParameterSlices];

    public long LastMessageStarted { get; private set; }

    public void AttachSession(PostgresSession session) => _session = session;

    public void Interrupt() => input.CancelPendingRead();

    public void Dispose()
    {
        lock (_timerGate)
        {
            _disposed = true;
            _activeReadCts = null;
            _readGeneration++;
            _timerScheduledDeadline = long.MaxValue;
            _readTimer?.Dispose();
            _readTimer = null;
        }

        _closedRegistration.Dispose();
        _readCts?.Dispose();
        _readCts = null;
    }

    public async ValueTask<PgStartupRead> ReadStartupAsync(TimeSpan timeout)
    {
        var called = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        while (true)
        {
            ReadResult read;
            try
            {
                read = await input.ReadAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return new PgIdleExpired();
            }

            var buffer = read.Buffer;
            if (buffer.Length >= 4)
            {
                var length = ReadInt32(buffer);
                if (length is < 8 or > PostgresLimits.MaxStartupBytes)
                {
                    input.AdvanceTo(buffer.End);
                    return PgError.ProtocolViolation("invalid startup packet length");
                }

                if (buffer.Length >= length)
                {
                    var packet = ParseStartupSequence(buffer.Slice(4, length - 4));
                    input.AdvanceTo(buffer.GetPosition(length));
                    return timeProvider.GetElapsedTime(called) >= timeout ? new PgIdleExpired() : packet;
                }
            }

            if (read.IsCompleted || read.IsCanceled)
            {
                input.AdvanceTo(buffer.End);
                return new PgPeerClosed();
            }

            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public ValueTask<PgMessageRead> ReadMessageAsync(TimeSpan timeout, PgReadDeadline? ceiling, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && _interruptedStart is null && input.TryRead(out var syncRead))
        {
            var buffer = syncRead.Buffer;
            if (buffer.Length >= 5)
            {
                var type = (char)buffer.FirstSpan[0];
                var length = ReadInt32(buffer.Slice(1));
                if (length < 4)
                {
                    input.AdvanceTo(buffer.End);
                    return ValueTask.FromResult<PgMessageRead>(PgError.ProtocolViolation($"message length {length} is out of bounds"));
                }

                if (length <= PostgresLimits.MaxMessageBytes && buffer.Length > length)
                {
                    var message = ParseMessageSequence(type, buffer.Slice(5, length - 4));
                    input.AdvanceTo(buffer.GetPosition(length + 1));
                    LastMessageStarted = timeProvider.GetTimestamp();
                    if (ceiling is { } limit && limit.Limit + limit.Grace <= TimeSpan.Zero)
                    {
                        return ValueTask.FromResult<PgMessageRead>(new PgIncompleteExpired());
                    }

                    return ValueTask.FromResult(message);
                }
            }

            if (syncRead.IsCanceled)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                return ValueTask.FromResult<PgMessageRead>(new PgReadInterrupted());
            }

            if (syncRead.IsCompleted && buffer.IsEmpty)
            {
                input.AdvanceTo(buffer.End);
                return ValueTask.FromResult<PgMessageRead>(new PgPeerClosed());
            }

            input.AdvanceTo(buffer.Start);
        }

        return ReadMessageSlowAsync(timeout, ceiling, cancellationToken);
    }

    private async ValueTask<PgMessageRead> ReadMessageSlowAsync(TimeSpan timeout, PgReadDeadline? ceiling, CancellationToken cancellationToken)
    {
        var called = timeProvider.GetTimestamp();
        var started = _interruptedStart;
        var seen = _interruptedSeen;
        _interruptedStart = null;
        _interruptedSeen = 0;
        var initial = started is { } resumed ? Remaining(resumed, seen, ceiling, called) : timeout;
        var cts = PrepareReadCts(cancellationToken);
        var generation = BeginRead(cts, initial);
        var oversized = 0L;
        var discarded = 0L;
        try
        {
            while (true)
            {
                ReadResult read;
                try
                {
                    read = await input.ReadAsync(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return started is null ? new PgIdleExpired() : new PgIncompleteExpired();
                }

                var buffer = read.Buffer;
                if (discarded + buffer.Length > seen)
                {
                    var first = started ??= timeProvider.GetTimestamp();
                    seen = discarded + buffer.Length;
                    UpdateReadDeadline(generation, Remaining(first, seen, ceiling, called));
                }

                if (oversized == 0 && buffer.Length >= 5)
                {
                    var type = (char)buffer.FirstSpan[0];
                    var length = ReadInt32(buffer.Slice(1));
                    if (length < 4)
                    {
                        input.AdvanceTo(buffer.End);
                        return PgError.ProtocolViolation($"message length {length} is out of bounds");
                    }

                    if (length > PostgresLimits.MaxMessageBytes)
                    {
                        oversized = length + 1L;
                    }
                    else if (buffer.Length > length)
                    {
                        var message = ParseMessageSequence(type, buffer.Slice(5, length - 4));
                        input.AdvanceTo(buffer.GetPosition(length + 1));
                        LastMessageStarted = started ?? timeProvider.GetTimestamp();
                        var late = timeProvider.GetElapsedTime(LastMessageStarted) >= PostgresLimits.MessageBudget(seen)
                            || (ceiling is { } limit && timeProvider.GetElapsedTime(called) >= Bound(limit, called, LastMessageStarted));
                        return late ? new PgIncompleteExpired() : message;
                    }
                }

                if (oversized > 0)
                {
                    var skipped = Math.Min(buffer.Length, oversized - discarded);
                    discarded += skipped;
                    input.AdvanceTo(buffer.GetPosition(skipped));
                    if (discarded == oversized || read.IsCompleted || read.IsCanceled)
                    {
                        return PgError.ProtocolViolation($"message length {oversized - 1} exceeds the limit of {PostgresLimits.MaxMessageBytes} bytes");
                    }

                    continue;
                }

                if (read.IsCanceled)
                {
                    input.AdvanceTo(buffer.Start, buffer.End);
                    _interruptedStart = buffer.IsEmpty ? null : started;
                    _interruptedSeen = buffer.IsEmpty ? 0 : seen;
                    return new PgReadInterrupted();
                }

                if (read.IsCompleted)
                {
                    input.AdvanceTo(buffer.End);
                    return new PgPeerClosed();
                }

                input.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            EndRead(generation);
        }
    }

    private long BeginRead(CancellationTokenSource cts, TimeSpan duration)
    {
        lock (_timerGate)
        {
            var generation = ++_readGeneration;
            if (_disposed)
            {
                return generation;
            }

            _activeReadCts = cts;
            ArmReadTimerLocked(cts, duration);
            return generation;
        }
    }

    private void UpdateReadDeadline(long generation, TimeSpan duration)
    {
        lock (_timerGate)
        {
            if (_disposed || _readGeneration != generation || _activeReadCts is not { } cts)
            {
                return;
            }

            ArmReadTimerLocked(cts, duration);
        }
    }

    private void ArmReadTimerLocked(CancellationTokenSource cts, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            _readDeadline = timeProvider.GetTimestamp();
            cts.Cancel();
            return;
        }

        var clamped = duration > MaxTimerDelay ? MaxTimerDelay : duration;
        var now = timeProvider.GetTimestamp();
        var deadline = AddTimestamp(now, clamped);
        _readDeadline = deadline;
        if (_readTimer is null)
        {
            _timerScheduledDeadline = deadline;
            _readTimer = timeProvider.CreateTimer(static state =>
            {
                if (state is PgFrameReader owner)
                {
                    owner.OnReadTimer();
                }
            }, this, clamped, Timeout.InfiniteTimeSpan);
        }
        else if (deadline < _timerScheduledDeadline)
        {
            _timerScheduledDeadline = deadline;
            _readTimer.Change(clamped, Timeout.InfiniteTimeSpan);
        }
    }

    private void EndRead(long generation)
    {
        lock (_timerGate)
        {
            if (_readGeneration == generation)
            {
                _activeReadCts = null;
                _readGeneration++;
            }
        }
    }

    private void OnReadTimer()
    {
        lock (_timerGate)
        {
            if (_disposed)
            {
                return;
            }

            _timerScheduledDeadline = long.MaxValue;
            if (_activeReadCts is not { } cts)
            {
                return;
            }

            var remaining = timeProvider.GetElapsedTime(timeProvider.GetTimestamp(), _readDeadline);
            if (remaining <= TimeSpan.Zero)
            {
                cts.Cancel();
                return;
            }

            var clamped = remaining > MaxTimerDelay ? MaxTimerDelay : remaining;
            _timerScheduledDeadline = _readDeadline;
            _readTimer?.Change(clamped, Timeout.InfiniteTimeSpan);
        }
    }

    private long AddTimestamp(long now, TimeSpan duration)
    {
        var ticks = timeProvider.TimestampFrequency == TimeSpan.TicksPerSecond
            ? duration.Ticks
            : (long)(duration.Ticks * ((double)timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond));
        return now > long.MaxValue - ticks ? long.MaxValue : now + ticks;
    }

    private CancellationTokenSource PrepareReadCts(CancellationToken cancellationToken)
    {
        if (_readCts is { } existing && existing.TryReset())
        {
            if (cancellationToken != _registeredToken)
            {
                _closedRegistration.Dispose();
                _registeredToken = cancellationToken;
                _closedRegistration = cancellationToken.CanBeCanceled
                    ? cancellationToken.UnsafeRegister(static state =>
                    {
                        if (state is CancellationTokenSource target)
                        {
                            target.Cancel();
                        }
                    }, existing)
                    : default;
            }

            return existing;
        }

        _closedRegistration.Dispose();
        _readCts?.Dispose();
        var created = new CancellationTokenSource();
        _readCts = created;
        _registeredToken = cancellationToken;
        _closedRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state =>
            {
                if (state is CancellationTokenSource target)
                {
                    target.Cancel();
                }
            }, created)
            : default;
        return created;
    }

    private TimeSpan Remaining(long first, long seen, PgReadDeadline? ceiling, long called)
    {
        var remaining = PostgresLimits.MessageBudget(seen) - timeProvider.GetElapsedTime(first);
        return ceiling is { } limit && Bound(limit, called, first) - timeProvider.GetElapsedTime(called) is var left && left < remaining ? left : remaining;
    }

    private TimeSpan Bound(PgReadDeadline ceiling, long called, long started) =>
        timeProvider.GetElapsedTime(called, started) < ceiling.Limit ? ceiling.Limit + ceiling.Grace : ceiling.Limit;

    private static PgStartupRead ParseStartupSequence(ReadOnlySequence<byte> body)
    {
        if (body.IsSingleSegment)
        {
            return ParseStartup(body.FirstSpan);
        }

        var rented = ArrayPool<byte>.Shared.Rent((int)body.Length);
        try
        {
            body.CopyTo(rented);
            return ParseStartup(rented.AsSpan(0, (int)body.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private PgMessageRead ParseMessageSequence(char type, ReadOnlySequence<byte> body)
    {
        if (body.IsSingleSegment)
        {
            return ParseMessage(type, body.FirstSpan);
        }

        var rented = ArrayPool<byte>.Shared.Rent((int)body.Length);
        try
        {
            body.CopyTo(rented);
            return ParseMessage(type, rented.AsSpan(0, (int)body.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int ReadInt32(ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= 4)
        {
            return BinaryPrimitives.ReadInt32BigEndian(buffer.FirstSpan);
        }

        Span<byte> header = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(header);
        return BinaryPrimitives.ReadInt32BigEndian(header);
    }

    private static PgStartupRead ParseStartup(ReadOnlySpan<byte> body)
    {
        var reader = new PgSpanReader(body);
        if (!reader.TryInt32(out var code))
        {
            return PgError.ProtocolViolation("invalid startup packet");
        }

        switch (code)
        {
            case SslCode:
                return new PgSslRequest();
            case GssCode:
                return new PgGssEncryptionRequest();
            case CancelCode:
                return reader.TryInt32(out var processId) && reader.TryInt32(out var secret)
                    ? new PgCancelRequest(processId, secret)
                    : PgError.ProtocolViolation("invalid cancel request");
        }

        if (code >> 16 != 3)
        {
            return new PgError("0A000", $"unsupported frontend protocol {code >> 16}.{code & 0xFFFF}: server supports 3.0", Fatal: true);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            if (!reader.TryCString(out var name))
            {
                return PgError.ProtocolViolation("invalid startup packet layout: expected terminator as last byte");
            }

            if (name.Length == 0)
            {
                return new PgStartupMessage(code, parameters);
            }

            if (!reader.TryCString(out var value))
            {
                return PgError.ProtocolViolation("invalid startup packet layout");
            }

            parameters[name] = value;
        }
    }

    private PgMessageRead ParseMessage(char type, ReadOnlySpan<byte> body)
    {
        var reader = new PgSpanReader(body);
        PgFrontendMessage? message = type switch
        {
            'p' => reader.TryCString(out var password) ? new PgPasswordMessage(password) : null,
            'Q' => ParseQuery(body),
            'P' => ParseParse(ref reader),
            'B' => ParseBind(ref reader),
            'D' => reader.TryByte(out var describeTarget) && reader.TryCString(out var describeName) && describeTarget is (byte)'S' or (byte)'P'
                ? _describeMessage.Reset((char)describeTarget, describeName)
                : null,
            'E' => reader.TryCString(out var portal) && reader.TryInt32(out var maxRows) ? _executeMessage.Reset(portal, maxRows) : null,
            'C' => reader.TryByte(out var closeTarget) && reader.TryCString(out var closeName) && closeTarget is (byte)'S' or (byte)'P'
                ? _closeMessage.Reset((char)closeTarget, closeName)
                : null,
            'S' => PgSyncMessage.Instance,
            'H' => PgFlushMessage.Instance,
            'X' => PgTerminateMessage.Instance,
            'd' => new PgCopyDataMessage(body.ToArray()),
            'c' => PgCopyDoneMessage.Instance,
            'f' => reader.TryCString(out var reason) ? new PgCopyFailMessage(reason) : null,
            'F' => PgFunctionCallMessage.Instance,
            _ => new PgUnknownMessage(type),
        };
        return message is null ? PgError.ProtocolViolation($"invalid message format for message type '{type}'") : message;
    }

    private PgQueryMessage? ParseQuery(ReadOnlySpan<byte> body)
    {
        if (body is not [.. var sqlUtf8, 0])
        {
            return null;
        }

        if (_session is { } session)
        {
            return session.TryResolveUtf8Sql(sqlUtf8, out var resolved, out var entry)
                ? _queryMessage.Reset(resolved, entry)
                : null;
        }

        return Utf8.IsValid(sqlUtf8)
            ? _queryMessage.Reset(sqlUtf8.IsEmpty ? string.Empty : Encoding.UTF8.GetString(sqlUtf8), null)
            : null;
    }

    private PgParseMessage? ParseParse(ref PgSpanReader reader)
    {
        if (!reader.TryCString(out var name) || !reader.TryCStringBytes(out var sqlUtf8) || !reader.TryInt16(out var count) || count < 0)
        {
            return null;
        }

        string sql;
        CachedParseEntry? entry;
        if (_session is { } session)
        {
            if (!session.TryResolveUtf8Sql(sqlUtf8, out sql, out entry))
            {
                return null;
            }
        }
        else
        {
            if (!Utf8.IsValid(sqlUtf8))
            {
                return null;
            }

            sql = sqlUtf8.IsEmpty ? string.Empty : Encoding.UTF8.GetString(sqlUtf8);
            entry = null;
        }

        if (count == 0)
        {
            return _parseMessage.Reset(name, sql, [], entry);
        }

        if (count == 1)
        {
            if (!reader.TryInt32(out var singleOid))
            {
                return null;
            }

            int[] singleOids = singleOid switch
            {
                0 => OidZero,
                PgOid.Int8 => OidInt8,
                PgOid.Int4 => OidInt4,
                PgOid.Text => OidText,
                _ => [singleOid],
            };
            return _parseMessage.Reset(name, sql, singleOids, entry);
        }

        if (count == 2)
        {
            if (!reader.TryInt32(out var firstOid) || !reader.TryInt32(out var secondOid))
            {
                return null;
            }

            int[] pairOids = (firstOid, secondOid) switch
            {
                (0, 0) => OidZeroZero,
                (PgOid.Int8, PgOid.Int8) => OidInt8Int8,
                _ => [firstOid, secondOid],
            };
            return _parseMessage.Reset(name, sql, pairOids, entry);
        }

        var oids = new int[count];
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryInt32(out oids[index]))
            {
                return null;
            }
        }

        return _parseMessage.Reset(name, sql, oids, entry);
    }

    private PgBindMessage? ParseBind(ref PgSpanReader reader)
    {
        try
        {
            if (!reader.TryCString(out var portal) || !reader.TryCString(out var statement) || !reader.TryInt16(out var formatCount) || formatCount < 0)
            {
                return null;
            }

            if (!TryReadFormats(ref reader, formatCount, out var formats))
            {
                return null;
            }

            if (!reader.TryInt16(out var parameterCount) || parameterCount < 0)
            {
                return null;
            }

            if (parameterCount > _parameterSlices.Length)
            {
                _parameterSlices = new PgParameterSlice[Math.Max(_parameterSlices.Length * 2, parameterCount)];
            }

            var bufferPos = 0;
            for (var index = 0; index < parameterCount; index++)
            {
                if (!reader.TryInt32(out var length) || length < -1)
                {
                    return null;
                }

                if (length == -1)
                {
                    _parameterSlices[index] = new PgParameterSlice(0, -1);
                    continue;
                }

                if (!reader.TrySlice(length, out var slice))
                {
                    return null;
                }

                if (bufferPos + length > _parameterBuffer.Length)
                {
                    Array.Resize(ref _parameterBuffer, Math.Max(_parameterBuffer.Length * 2, bufferPos + length));
                }

                slice.CopyTo(_parameterBuffer.AsSpan(bufferPos));
                _parameterSlices[index] = new PgParameterSlice(bufferPos, length);
                bufferPos += length;
            }

            if (!reader.TryInt16(out var resultCount) || resultCount < 0)
            {
                return null;
            }

            if (!TryReadFormats(ref reader, resultCount, out var results))
            {
                return null;
            }

            return _bindMessage.Reset(portal, statement, formats, _parameterBuffer, _parameterSlices, parameterCount, results);
        }
        finally
        {
            if (_parameterBuffer.Length > MaxRetainedParameterBytes)
            {
                _parameterBuffer = new byte[InitialParameterBufferBytes];
            }

            if (_parameterSlices.Length > MaxRetainedParameterSlices)
            {
                _parameterSlices = new PgParameterSlice[InitialParameterSlices];
            }
        }
    }

    private static bool TryReadFormats(ref PgSpanReader reader, short count, out short[] formats)
    {
        if (count == 0)
        {
            formats = [];
            return true;
        }

        if (count == 1)
        {
            if (!reader.TryInt16(out var single) || single is not (0 or 1))
            {
                formats = [];
                return false;
            }

            formats = single == 0 ? FormatZero : FormatOne;
            return true;
        }

        formats = new short[count];
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryInt16(out formats[index]) || formats[index] is not (0 or 1))
            {
                return false;
            }
        }

        return true;
    }

    private ref struct PgSpanReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        public bool TryByte(out byte value)
        {
            value = 0;
            if (_data.IsEmpty)
            {
                return false;
            }

            value = _data[0];
            _data = _data[1..];
            return true;
        }

        public bool TryInt16(out short value)
        {
            value = 0;
            if (_data.Length < 2)
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt16BigEndian(_data);
            _data = _data[2..];
            return true;
        }

        public bool TryInt32(out int value)
        {
            value = 0;
            if (_data.Length < 4)
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt32BigEndian(_data);
            _data = _data[4..];
            return true;
        }

        public bool TrySlice(int count, out ReadOnlySpan<byte> value)
        {
            if (count > _data.Length)
            {
                value = [];
                return false;
            }

            value = _data[..count];
            _data = _data[count..];
            return true;
        }

        public bool TryCStringBytes(out ReadOnlySpan<byte> value)
        {
            var end = _data.IndexOf((byte)0);
            if (end < 0)
            {
                value = [];
                return false;
            }

            value = _data[..end];
            _data = _data[(end + 1)..];
            return true;
        }

        public bool TryCString(out string value)
        {
            value = string.Empty;
            var end = _data.IndexOf((byte)0);
            if (end < 0)
            {
                return false;
            }

            if (end == 0)
            {
                _data = _data[1..];
                return true;
            }

            var slice = _data[..end];
            if (!Utf8.IsValid(slice))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(slice);
            _data = _data[(end + 1)..];
            return true;
        }
    }
}
