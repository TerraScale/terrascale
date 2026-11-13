using Microsoft.AspNetCore.Http.Features;

namespace TerraBase.Features.Sql;

internal sealed class SqlSocketInput
{
    private int _header;
    private bool _invalid;
    private int _extended;
    private ulong _remaining;
    private bool _masked;
    private bool _message;
    private bool _final;
    private bool _control;

    public Action? Started
    {
        get;
        set
        {
            field = value;
            if (_message)
            {
                field?.Invoke();
            }
        }
    }

    public static void Wrap(HttpContext context)
    {
        var upgrade = context.Features.Get<IHttpUpgradeFeature>();
        var connect = context.Features.Get<IHttpExtendedConnectFeature>();
        if (upgrade is not { IsUpgradableRequest: true } && connect is not { IsExtendedConnect: true, Protocol: "websocket" })
        {
            return;
        }

        var input = new SqlSocketInput();
        context.Features.Set(input);
        if (upgrade is { IsUpgradableRequest: true })
        {
            context.Features.Set<IHttpUpgradeFeature>(new Upgrade(upgrade, input));
        }

        if (connect is { IsExtendedConnect: true, Protocol: "websocket" })
        {
            context.Features.Set<IHttpExtendedConnectFeature>(new Connect(connect, input));
        }
    }

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        while (!_invalid && !bytes.IsEmpty)
        {
            if (_header == 0)
            {
                _control = (bytes[0] & 8) != 0;
                _final = (bytes[0] & 128) != 0;
                if (!_control)
                {
                    _message = true;
                    Started?.Invoke();
                }

                _header = 1;
            }
            else if (_header == 1)
            {
                _masked = (bytes[0] & 128) != 0;
                var length = bytes[0] & 127;
                _extended = length switch { 126 => 2, 127 => 8, _ => 0 };
                _remaining = _extended == 0 ? (ulong)length : 0;
                _header = _extended > 0 ? 2 : 3;
                if (_extended == 0)
                {
                    _remaining = checked(_remaining + (_masked ? 4UL : 0));
                }
            }
            else if (_header == 2)
            {
                if (_extended == 8 && (bytes[0] & 128) != 0)
                {
                    _invalid = true;
                    Started = null;
                    return;
                }

                _remaining = checked((_remaining << 8) | bytes[0]);
                if (--_extended == 0)
                {
                    _remaining = checked(_remaining + (_masked ? 4UL : 0));
                    _header = 3;
                }
            }
            else
            {
                var count = (int)Math.Min((ulong)bytes.Length, _remaining);
                _remaining -= (ulong)count;
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
    }

    private void Finish()
    {
        _header = 0;
        if (!_control && _final)
        {
            _message = false;
        }
    }

    private sealed class Upgrade(IHttpUpgradeFeature inner, SqlSocketInput input) : IHttpUpgradeFeature
    {
        public bool IsUpgradableRequest => inner.IsUpgradableRequest;

        public async Task<Stream> UpgradeAsync() => new Transport(await inner.UpgradeAsync().ConfigureAwait(false), input);
    }

    private sealed class Connect(IHttpExtendedConnectFeature inner, SqlSocketInput input) : IHttpExtendedConnectFeature
    {
        public bool IsExtendedConnect => inner.IsExtendedConnect;
        public string? Protocol => inner.Protocol;

        public async ValueTask<Stream> AcceptAsync() => new Transport(await inner.AcceptAsync().ConfigureAwait(false), input);
    }

    private sealed class Transport(Stream inner, SqlSocketInput input) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            input.Inspect(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            input.Inspect(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
