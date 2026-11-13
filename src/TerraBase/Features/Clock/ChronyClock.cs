using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace TerraBase.Features.Clock;

internal sealed class ChronyClock : IClockErrorSource
{
    public async ValueTask<ClockTracking?> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var request = new byte[104];
        request[0] = 6;
        request[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 33);
        RandomNumberGenerator.Fill(request.AsSpan(8, 4));
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 323), timeout.Token).ConfigureAwait(false);
            await socket.SendAsync(request, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            var response = new byte[105];
            var length = await socket.ReceiveAsync(response, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            if (length != 104 || response[0] != 6 || response[1] != 2 || response[2] != 0 || response[3] != 0 ||
                BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4)) != 33 ||
                BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)) != 5 ||
                BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8)) != 0 ||
                !response.AsSpan(16, 4).SequenceEqual(request.AsSpan(8, 4)) ||
                BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(52)) is 0 or > 15 ||
                BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(54)) > 2)
            {
                return null;
            }

            var seconds = BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(56, 8));
            var nanoseconds = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(64, 4));
            var correction = DecodeFloat(response.AsSpan(68, 4)) * 1000;
            var delay = DecodeFloat(response.AsSpan(92, 4)) * 1000;
            var dispersion = DecodeFloat(response.AsSpan(96, 4)) * 1000;
            if (seconds is 0 or > 253402300798 || nanoseconds >= 1_000_000_000 || delay < 0 || dispersion < 0 ||
                !double.IsFinite(correction) || !double.IsFinite(delay) || !double.IsFinite(dispersion))
            {
                return null;
            }

            var reference = DateTimeOffset.FromUnixTimeSeconds((long)seconds).AddTicks(nanoseconds / 100);
            return new ClockTracking(reference, correction, dispersion + delay / 2);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static double DecodeFloat(ReadOnlySpan<byte> value)
    {
        var bits = BinaryPrimitives.ReadInt32BigEndian(value);
        var exponent = (bits >> 25) - 25;
        var coefficient = (bits << 7) >> 7;
        return Math.ScaleB(coefficient, exponent);
    }
}
