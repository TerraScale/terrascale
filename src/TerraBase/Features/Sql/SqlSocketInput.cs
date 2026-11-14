using System.Net.Sockets;
using System.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using TerraBase.Features.Qos;

namespace TerraBase.Features.Sql;

internal sealed class SqlSocketInput(TimeProvider time, Socket? socket = null)
{
    private readonly Lock _gate = new();
    private readonly Queue<Message> _messages = new();
    private Message? _current;
    private int _header;
    private bool _invalid;
    private int _extended;
    private ulong _remaining;
    private bool _masked;
    private bool _message;
    private bool _final;
    private bool _control;

    private int _maskBytes;
    private Stream? _transport;

    public Action<int, long>? Progress
    {
        get;
        set
        {
            lock (_gate)
            {
                field = value;
                if (_messages.TryPeek(out var first))
                {
                    field?.Invoke(first.Received, first.Started);
                }
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _messages.TryDequeue(out _);
            Progress = null;
        }
    }

    public async Task DrainAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new InvalidOperationException("the WebSocket transport has not been accepted");
        while (await transport.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
        {
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

        if (context.Features.Get<IHttpMinResponseDataRateFeature>() is { } responseRate)
        {
            responseRate.MinDataRate = null;
        }

        var input = new SqlSocketInput(context.RequestServices.GetRequiredService<TimeProvider>(), context.Features.GetRequiredFeature<IConnectionSocketFeature>().Socket);
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

    public HttpMessageInvoker Observe(HttpClient http) => new(new Forward(http, this));

    private sealed class Forward(HttpClient http, SqlSocketInput input) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.Content = new ObservedContent(response.Content, input);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                http.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ObservedContent : HttpContent
    {
        private readonly HttpContent _content;
        private readonly SqlSocketInput _input;

        public ObservedContent(HttpContent content, SqlSocketInput input)
        {
            (_content, _input) = (content, input);
            foreach (var header in content.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _content.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _content.CopyToAsync(stream, cancellationToken);

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => new Transport(_content.ReadAsStream(cancellationToken), _input);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new Transport(await _content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _input);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _content.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            InspectLocked(bytes);
        }
    }

    private void InspectLocked(ReadOnlySpan<byte> bytes)
    {
        while (!_invalid && !bytes.IsEmpty)
        {
            if (_header == 0)
            {
                _control = (bytes[0] & 8) != 0;
                _final = (bytes[0] & 128) != 0;
                if (!_control)
                {
                    if (!_message)
                    {
                        if (_messages.Count >= 256)
                        {
                            throw new InvalidDataException("too many buffered WebSocket messages");
                        }

                        _current = new Message(time.GetTimestamp());
                        _messages.Enqueue(_current);
                        _message = true;
                    }

                    Report(0);
                }

                _header = 1;
            }
            else if (_header == 1)
            {
                _masked = (bytes[0] & 128) != 0;
                _maskBytes = _masked ? 4 : 0;
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
                    Progress = null;
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
                if (!_control)
                {
                    var mask = Math.Min(count, _maskBytes);
                    _maskBytes -= mask;
                    Report(count - mask);
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
    }

    private void Report(int bytes)
    {
        if (_current is { } current)
        {
            current.Received += bytes;
            if (_messages.TryPeek(out var first) && ReferenceEquals(first, current))
            {
                Progress?.Invoke(bytes, current.Started);
            }
        }
    }

    private void Finish()
    {
        _header = 0;
        if (!_control && _final)
        {
            _message = false;
            _current = null;
        }
    }

    private sealed class Message(long started)
    {
        public long Started { get; } = started;
        public int Received { get; set; }
    }

    private sealed class Upgrade(IHttpUpgradeFeature inner, SqlSocketInput input) : IHttpUpgradeFeature
    {
        public bool IsUpgradableRequest => inner.IsUpgradableRequest;

        public async Task<Stream> UpgradeAsync() => input._transport = new Transport(await inner.UpgradeAsync().ConfigureAwait(false), input);
    }

    private sealed class Connect(IHttpExtendedConnectFeature inner, SqlSocketInput input) : IHttpExtendedConnectFeature
    {
        public bool IsExtendedConnect => inner.IsExtendedConnect;
        public string? Protocol => inner.Protocol;

        public async ValueTask<Stream> AcceptAsync() => input._transport = new Transport(await inner.AcceptAsync().ConfigureAwait(false), input);
    }

    private async ValueTask WriteAsync(Stream transport, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        using var deadline = socket is { } connection ? new SessionSendDeadline(connection, time, cancellationToken) : null;
        await transport.WriteAsync(buffer, deadline?.Token ?? cancellationToken).ConfigureAwait(false);
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
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => input.WriteAsync(inner, buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => input.WriteAsync(inner, buffer.AsMemory(offset, count), cancellationToken).AsTask();

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
