using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Text.Unicode;
using TerraBase.Postgres.Hosting;

namespace TerraBase.Postgres.Protocol;

internal sealed class PgFrameReader(PipeReader input, TimeProvider timeProvider)
{
    public const int ProtocolVersion3 = 196608;
    private const int CancelCode = 80877102;
    private const int SslCode = 80877103;
    private const int GssCode = 80877104;

    public void Interrupt() => input.CancelPendingRead();

    public async ValueTask<PgStartupRead> ReadStartupAsync(TimeSpan timeout)
    {
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
                    var packet = Parse(buffer.Slice(4, length - 4), static body => ParseStartup(body));
                    input.AdvanceTo(buffer.GetPosition(length));
                    return packet;
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

    public long LastMessageStarted { get; private set; }

    public async ValueTask<PgMessageRead> ReadMessageAsync(TimeSpan timeout, TimeSpan? ceiling, CancellationToken cancellationToken)
    {
        var called = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        long? started = null;
        var seen = 0L;
        var oversized = 0L;
        var discarded = 0L;
        while (true)
        {
            ReadResult read;
            try
            {
                read = await input.ReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return started is null ? new PgIdleExpired() : new PgIncompleteExpired();
            }

            var buffer = read.Buffer;
            if (discarded + buffer.Length > seen)
            {
                var first = started ??= timeProvider.GetTimestamp();
                seen = discarded + buffer.Length;
                var remaining = PostgresLimits.MessageBudget(seen) - timeProvider.GetElapsedTime(first);
                if (ceiling is { } limit && limit - timeProvider.GetElapsedTime(called) is var left && left < remaining)
                {
                    remaining = left;
                }

                deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
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
                    var message = Parse(buffer.Slice(5, length - 4), body => ParseMessage(type, body));
                    input.AdvanceTo(buffer.GetPosition(length + 1));
                    LastMessageStarted = started ?? timeProvider.GetTimestamp();
                    return message;
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

    private delegate TResult SpanParser<out TResult>(ReadOnlySpan<byte> body);

    private static TResult Parse<TResult>(ReadOnlySequence<byte> body, SpanParser<TResult> parser)
    {
        if (body.IsSingleSegment)
        {
            return parser(body.FirstSpan);
        }

        var rented = ArrayPool<byte>.Shared.Rent((int)body.Length);
        try
        {
            body.CopyTo(rented);
            return parser(rented.AsSpan(0, (int)body.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int ReadInt32(ReadOnlySequence<byte> buffer)
    {
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

    private static PgMessageRead ParseMessage(char type, ReadOnlySpan<byte> body)
    {
        var reader = new PgSpanReader(body);
        PgFrontendMessage? message = type switch
        {
            'p' => reader.TryCString(out var password) ? new PgPasswordMessage(password) : null,
            'Q' => reader.TryCString(out var sql) ? new PgQueryMessage(sql) : null,
            'P' => ParseParse(ref reader),
            'B' => ParseBind(ref reader),
            'D' => reader.TryByte(out var describeTarget) && reader.TryCString(out var describeName) && describeTarget is (byte)'S' or (byte)'P'
                ? new PgDescribeMessage((char)describeTarget, describeName)
                : null,
            'E' => reader.TryCString(out var portal) && reader.TryInt32(out var maxRows) ? new PgExecuteMessage(portal, maxRows) : null,
            'C' => reader.TryByte(out var closeTarget) && reader.TryCString(out var closeName) && closeTarget is (byte)'S' or (byte)'P'
                ? new PgCloseMessage((char)closeTarget, closeName)
                : null,
            'S' => new PgSyncMessage(),
            'H' => new PgFlushMessage(),
            'X' => new PgTerminateMessage(),
            'd' => new PgCopyDataMessage(body.ToArray()),
            'c' => new PgCopyDoneMessage(),
            'f' => reader.TryCString(out var reason) ? new PgCopyFailMessage(reason) : null,
            'F' => new PgFunctionCallMessage(),
            _ => new PgUnknownMessage(type),
        };
        return message is null ? PgError.ProtocolViolation($"invalid message format for message type '{type}'") : message;
    }

    private static PgParseMessage? ParseParse(ref PgSpanReader reader)
    {
        if (!reader.TryCString(out var name) || !reader.TryCString(out var sql) || !reader.TryInt16(out var count) || count < 0)
        {
            return null;
        }

        var oids = new int[count];
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryInt32(out oids[index]))
            {
                return null;
            }
        }

        return new PgParseMessage(name, sql, oids);
    }

    private static PgBindMessage? ParseBind(ref PgSpanReader reader)
    {
        if (!reader.TryCString(out var portal) || !reader.TryCString(out var statement) || !reader.TryInt16(out var formatCount) || formatCount < 0)
        {
            return null;
        }

        var formats = new short[formatCount];
        for (var index = 0; index < formatCount; index++)
        {
            if (!reader.TryInt16(out formats[index]) || formats[index] is not (0 or 1))
            {
                return null;
            }
        }

        if (!reader.TryInt16(out var parameterCount) || parameterCount < 0)
        {
            return null;
        }

        var parameters = new byte[]?[parameterCount];
        for (var index = 0; index < parameterCount; index++)
        {
            if (!reader.TryInt32(out var length) || length < -1)
            {
                return null;
            }

            if (length == -1)
            {
                continue;
            }

            if (!reader.TryBytes(length, out var value))
            {
                return null;
            }

            parameters[index] = value;
        }

        if (!reader.TryInt16(out var resultCount) || resultCount < 0)
        {
            return null;
        }

        var results = new short[resultCount];
        for (var index = 0; index < resultCount; index++)
        {
            if (!reader.TryInt16(out results[index]) || results[index] is not (0 or 1))
            {
                return null;
            }
        }

        return new PgBindMessage(portal, statement, formats, parameters, results);
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

        public bool TryBytes(int count, out byte[] value)
        {
            value = [];
            if (count > _data.Length)
            {
                return false;
            }

            value = _data[..count].ToArray();
            _data = _data[count..];
            return true;
        }

        public bool TryCString(out string value)
        {
            value = string.Empty;
            var end = _data.IndexOf((byte)0);
            if (end < 0 || !Utf8.IsValid(_data[..end]))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(_data[..end]);
            _data = _data[(end + 1)..];
            return true;
        }
    }
}
