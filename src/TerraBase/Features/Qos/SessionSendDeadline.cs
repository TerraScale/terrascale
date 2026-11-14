using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TerraBase.Features.Qos;

internal sealed class SessionSendDeadline : IDisposable
{
    private const int TcpProtocol = 6;
    private const int TcpInfo = 11;
    private const int AcknowledgedBytesOffset = 120;
    private readonly Lock _gate = new();
    private readonly Socket _socket;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop;
    private readonly ITimer _timer;
    private ulong _acknowledged;
    private long _progress;
    private bool _disposed;
    private bool _cancelling;

    public SessionSendDeadline(Socket socket, TimeProvider time, CancellationToken cancellationToken)
    {
        _socket = socket;
        _time = time;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _progress = time.GetTimestamp();
        try
        {
            _acknowledged = Acknowledged();
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            _stop.Cancel();
        }

        _timer = time.CreateTimer(_ => Check(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public CancellationToken Token => _stop.Token;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            if (!_cancelling)
            {
                _stop.Dispose();
            }
        }
    }

    private ulong Acknowledged()
    {
        Span<byte> info = stackalloc byte[AcknowledgedBytesOffset + sizeof(ulong)];
        if (_socket.GetRawSocketOption(TcpProtocol, TcpInfo, info) < info.Length)
        {
            throw new IOException("the socket did not provide its acknowledged byte count");
        }

        return MemoryMarshal.Read<ulong>(info[AcknowledgedBytesOffset..]);
    }

    private void Check()
    {
        lock (_gate)
        {
            if (_disposed || _cancelling || _stop.IsCancellationRequested)
            {
                return;
            }

            var expired = false;
            try
            {
                var acknowledged = Acknowledged();
                if (acknowledged != _acknowledged)
                {
                    _acknowledged = acknowledged;
                    _progress = _time.GetTimestamp();
                }

                expired = _time.GetElapsedTime(_progress) >= QosSessions.MessageTimeout;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                expired = true;
            }

            if (!expired)
            {
                return;
            }

            _cancelling = true;
        }

        try
        {
            _stop.Cancel();
        }
        finally
        {
            lock (_gate)
            {
                _cancelling = false;
                if (_disposed)
                {
                    _stop.Dispose();
                }
            }
        }
    }
}
