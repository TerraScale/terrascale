using Microsoft.AspNetCore.Http.Features;

namespace TerraBase.Features.Sql;

internal readonly record struct ArrivedMessage(long Started, long Completed, long Sequence);

internal sealed class SocketInput(TimeProvider time)
{
    private static readonly TimeSpan ProtocolErrorCloseBudget = TimeSpan.FromSeconds(2);

    private const int MaxQueuedMessages = 256;

    private static readonly byte[] ProtocolErrorClose = [0x88, 0x02, 0x03, 0xEA];
    private static readonly TimeSpan ProtocolErrorDrainTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private MessageEntry[] _entries = new MessageEntry[8];
    private int _head;
    private int _count;
    private int _currentIndex = -1;
    private int _header;
    private int _extended;
    private ulong _remaining;
    private int _maskBytes;
    private bool _masked;
    private bool _message;
    private bool _final;
    private bool _control;
    private bool _invalid;
    private long _arrivedAt;
    private long _sequence;

    public Action<long, int, long>? Progress
    {
        get;
        set
        {
            lock (_gate)
            {
                field = value;
                if (_count > 0)
                {
                    ref readonly var first = ref _entries[_head];
                    if (!first.IsCompleted)
                    {
                        field?.Invoke(first.Started, first.Received, first.Sequence);
                    }
                }
            }
        }
    }

    public static void Wrap(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsConnect(method))
        {
            return;
        }

        var upgrade = context.Features.Get<IHttpUpgradeFeature>();
        var connect = context.Features.Get<IHttpExtendedConnectFeature>();
        if (upgrade is not { IsUpgradableRequest: true } && connect is not { IsExtendedConnect: true })
        {
            return;
        }

        var input = new SocketInput(context.RequestServices.GetRequiredService<TimeProvider>());
        context.Features.Set(input);
        if (upgrade is { IsUpgradableRequest: true })
        {
            context.Features.Set<IHttpUpgradeFeature>(new Upgrade(upgrade, input));
        }

        if (connect is { IsExtendedConnect: true })
        {
            context.Features.Set<IHttpExtendedConnectFeature>(new Connect(connect, input));
        }
    }

    public Action? Failed { get; set; }

    private TimeProvider Clock => time;

    public ArrivedMessage? Complete()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return null;
            }

            var message = _entries[_head];
            if (_currentIndex == _head)
            {
                _currentIndex = -1;
            }

            _head = (_head + 1) & (_entries.Length - 1);
            _count--;

            if (_count > 0)
            {
                ref readonly var next = ref _entries[_head];
                if (!next.IsCompleted)
                {
                    Progress?.Invoke(next.Started, next.Received, next.Sequence);
                }
            }

            return new ArrivedMessage(message.Started, message.IsCompleted ? message.Completed : time.GetTimestamp(), message.Sequence);
        }
    }

    private bool Inspect(ReadOnlySpan<byte> bytes, long arrivedAt)
    {
        lock (_gate)
        {
            var valid = !_invalid;
            _arrivedAt = arrivedAt;
            InspectLocked(bytes);
            return valid && _invalid;
        }
    }

    private void InspectLocked(ReadOnlySpan<byte> bytes)
    {
        var progressed = false;
        while (!_invalid && !bytes.IsEmpty)
        {
            if (_header == 0)
            {
                _control = (bytes[0] & 8) != 0;
                _final = (bytes[0] & 128) != 0;
                if (!_control && !_message)
                {
                    if (_count >= MaxQueuedMessages)
                    {
                        _invalid = true;
                        return;
                    }

                    if (_count == _entries.Length)
                    {
                        GrowEntries();
                    }

                    var tail = (_head + _count) & (_entries.Length - 1);
                    _entries[tail] = new MessageEntry(_arrivedAt, ++_sequence);
                    _currentIndex = tail;
                    _count++;
                    _message = true;
                }

                if (!_control)
                {
                    progressed = true;
                }

                _header = 1;
            }
            else if (_header == 1)
            {
                _masked = (bytes[0] & 128) != 0;
                _maskBytes = _masked ? 4 : 0;
                var length = bytes[0] & 127;
                _extended = length switch
                {
                    126 => 2,
                    127 => 8,
                    _ => 0,
                };
                _remaining = _extended == 0 ? (ulong)length + (ulong)_maskBytes : 0;
                _header = _extended > 0 ? 2 : 3;
            }
            else if (_header == 2)
            {
                if (_extended == 8 && (bytes[0] & 128) != 0)
                {
                    _invalid = true;
                    return;
                }

                _remaining = (_remaining << 8) | bytes[0];
                if (--_extended == 0)
                {
                    _remaining += (ulong)_maskBytes;
                    _header = 3;
                }
            }
            else
            {
                var count = (int)Math.Min((ulong)bytes.Length, _remaining);
                _remaining -= (ulong)count;
                if (!_control)
                {
                    var mask = Math.Min(count, _maskBytes);
                    _maskBytes -= mask;
                    if (_currentIndex >= 0)
                    {
                        _entries[_currentIndex].Received += count - mask;
                    }

                    progressed = true;
                }

                bytes = bytes[count..];
                if (_remaining == 0)
                {
                    Finish();
                }

                continue;
            }

            bytes = bytes[1..];
            if (_header == 3 && _remaining == 0)
            {
                Finish();
            }
        }

        if (progressed && !_invalid && _count > 0)
        {
            ref readonly var head = ref _entries[_head];
            if (!head.IsCompleted)
            {
                Progress?.Invoke(head.Started, head.Received, head.Sequence);
            }
        }
    }

    private void GrowEntries()
    {
        var next = new MessageEntry[_entries.Length * 2];
        var tailLength = _entries.Length - _head;
        _entries.AsSpan(_head, tailLength).CopyTo(next);
        if (_head > 0)
        {
            _entries.AsSpan(0, _head).CopyTo(next.AsSpan(tailLength));
        }

        _head = 0;
        _entries = next;
    }

    private void Finish()
    {
        _header = 0;
        if (!_control && _final)
        {
            if (_currentIndex >= 0)
            {
                ref var current = ref _entries[_currentIndex];
                current.Completed = _arrivedAt;
                current.IsCompleted = true;
                _currentIndex = -1;
            }

            _message = false;
        }
    }

    private struct MessageEntry(long started, long sequence)
    {
        public long Started = started;
        public long Sequence = sequence;
        public int Received;
        public long Completed;
        public bool IsCompleted;
    }

    private sealed class Upgrade(IHttpUpgradeFeature inner, SocketInput input) : IHttpUpgradeFeature
    {
        public bool IsUpgradableRequest => inner.IsUpgradableRequest;

        public async Task<Stream> UpgradeAsync() => new Transport(await inner.UpgradeAsync(), input);
    }

    private sealed class Connect(IHttpExtendedConnectFeature inner, SocketInput input) : IHttpExtendedConnectFeature
    {
        public bool IsExtendedConnect => inner.IsExtendedConnect;

        public string? Protocol => inner.Protocol;

        public async ValueTask<Stream> AcceptAsync() => new Transport(await inner.AcceptAsync(), input);
    }

    private sealed class Transport(Stream inner, SocketInput input) : Stream
    {
        private readonly SemaphoreSlim _writes = new(1, 1);

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (input.Inspect(buffer.Span[..read], input.Clock.GetTimestamp()))
            {
                input.Failed?.Invoke();
                await CloseAsync();
                await DrainPeerCloseAsync(cancellationToken);
            }

            return read;
        }

        private async ValueTask CloseAsync()
        {
            using var bounded = new CancellationTokenSource(ProtocolErrorCloseBudget, input.Clock);
            try
            {
                await WriteAsync(ProtocolErrorClose, bounded.Token);
                await inner.FlushAsync(bounded.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        private async ValueTask DrainPeerCloseAsync(CancellationToken cancellationToken)
        {
            var scratch = new byte[64];
            using var linger = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linger.CancelAfter(ProtocolErrorDrainTimeout);
            try
            {
                while (await inner.ReadAsync(scratch, linger.Token) > 0)
                {
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }

            if (_writes.Wait(0, CancellationToken.None))
            {
                return WriteAcquiredAsync(buffer, cancellationToken);
            }

            return WriteContendedAsync(buffer, cancellationToken);
        }

        private ValueTask WriteAcquiredAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var handedOff = false;
            try
            {
                var write = inner.WriteAsync(buffer, cancellationToken);
                if (write.IsCompletedSuccessfully)
                {
                    return ValueTask.CompletedTask;
                }

                handedOff = true;
                return FinishWriteAsync(write);
            }
            finally
            {
                if (!handedOff)
                {
                    _writes.Release();
                }
            }
        }

        private async ValueTask FinishWriteAsync(ValueTask write)
        {
            try
            {
                await write;
            }
            finally
            {
                _writes.Release();
            }
        }

        private async ValueTask WriteContendedAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            await _writes.WaitAsync(cancellationToken);
            try
            {
                await inner.WriteAsync(buffer, cancellationToken);
            }
            finally
            {
                _writes.Release();
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _writes.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

internal static class SocketInputExtensions
{
    extension(WebApplication app)
    {
        public WebApplication UseTerraBaseSocketInput()
        {
            app.Use((context, next) =>
            {
                SocketInput.Wrap(context);
                return next(context);
            });
            return app;
        }
    }
}
