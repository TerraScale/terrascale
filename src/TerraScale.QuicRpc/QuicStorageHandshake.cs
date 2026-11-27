using System.Buffers;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal sealed record StorageHello(string StorageFormatId) : IBinaryMessage<StorageHello>
{
    public void WriteTo(IBufferWriter<byte> writer) => writer.WriteString(StorageFormatId);

    public static StorageHello ReadFrom(ref WireReader reader) => new(reader.ReadString());
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal static class QuicStorageHandshake
{
    private static readonly SslApplicationProtocol LegacyProtocol = new(QuicProtocol.ApplicationProtocol);
    private static readonly SslApplicationProtocol MuxProtocol = new("tb/2-mux");
    private static readonly SslApplicationProtocol StorageMuxProtocol = new("tb/2-storage-mux");

    public static SslApplicationProtocol Protocol { get; } = new("tb/2-storage");

    public static List<SslApplicationProtocol> Protocols(string? storageFormatId) =>
        storageFormatId is null
            ? [MuxProtocol, LegacyProtocol]
            : [StorageMuxProtocol, Protocol, LegacyProtocol];

    public static bool RequiresStorageHandshake(SslApplicationProtocol negotiated) =>
        negotiated == StorageMuxProtocol || negotiated == Protocol;

    public static bool SupportsPersistentUnary(SslApplicationProtocol negotiated) =>
        negotiated == StorageMuxProtocol || negotiated == MuxProtocol;

    public static async ValueTask<QuicFault?> OfferAsync(QuicConnection connection, string localFormat, CallScope scope)
    {
        var opened = await QuicIo.OpenStreamAsync(connection, QuicStreamType.Bidirectional, scope);
        return opened switch
        {
            QuicStream stream => await ExchangeAsync(stream, localFormat, scope),
            QuicFault fault => fault,
        };
    }

    public static async ValueTask<QuicFault?> AcceptAsync(QuicConnection connection, string localFormat, CallScope scope)
    {
        var accepted = await QuicIo.AcceptStreamAsync(connection, scope);
        return accepted switch
        {
            QuicStream stream => await ReplyAsync(stream, localFormat, scope),
            QuicFault fault => fault,
        };
    }

    private static async ValueTask<QuicFault?> ExchangeAsync(QuicStream stream, string localFormat, CallScope scope)
    {
        try
        {
            using var request = FrameBuilder.Encode(new StorageHello(localFormat));
            var sent = await QuicIo.WriteAsync(stream, Encode(request), true, scope);
            return sent switch
            {
                QuicSent => await ReceiveReplyAsync(stream, localFormat, scope),
                QuicFault fault => fault,
            };
        }
        finally
        {
            await QuicIo.DisposeStreamAsync(stream);
        }
    }

    private static async ValueTask<QuicFault?> ReceiveReplyAsync(QuicStream stream, string localFormat, CallScope scope)
    {
        var read = await QuicIo.ReadSingleFrameAsync(stream, scope);
        return read switch
        {
            PooledFrame frame => CheckReply(frame, localFormat),
            StreamEnded => new ProtocolFault("The peer closed the storage hello without a response."),
            QuicFault fault => fault,
        };
    }

    private static QuicFault? CheckReply(PooledFrame frame, string localFormat)
    {
        var reply = QuicRpcClientCore.Decode<StorageHello>(frame, QuicMessageType.StorageHello, 0);
        return reply switch
        {
            StorageHello hello => Compare(localFormat, hello.StorageFormatId),
            QuicRemoteError { Code: QuicRemoteError.StorageFormatMismatch } refusal => new StorageFormatMismatch(refusal.Message),
            QuicRemoteError error => new ProtocolFault(error.Message),
            QuicUnavailable unavailable => new ProtocolFault(unavailable.Message),
            QuicTimeout timeout => new DeadlineExceeded(timeout.Message),
        };
    }

    private static async ValueTask<QuicFault?> ReplyAsync(QuicStream stream, string localFormat, CallScope scope)
    {
        try
        {
            var read = await QuicIo.ReadSingleFrameAsync(stream, scope);
            return read switch
            {
                PooledFrame frame => await ReplyToHelloAsync(stream, frame, localFormat, scope),
                StreamEnded => new ProtocolFault("The peer closed the storage hello without a request."),
                QuicFault fault => fault,
            };
        }
        finally
        {
            await QuicIo.DisposeStreamAsync(stream);
        }
    }

    private static async ValueTask<QuicFault?> ReplyToHelloAsync(QuicStream stream, PooledFrame frame, string localFormat, CallScope scope)
    {
        using (frame)
        {
            if (stream.Type != QuicStreamType.Bidirectional
                || frame.Header is not { MessageType: QuicMessageType.StorageHello, RequestId: 0, Control: QuicProtocol.ControlEndOfStream }
                || !BinaryMessage.TryDecode<StorageHello>(frame.Payload.Span, out var hello))
            {
                return new ProtocolFault("The first QUIC stream must carry a valid storage hello.");
            }

            var mismatch = Compare(localFormat, hello.StorageFormatId);
            using var response = mismatch is null
                ? FrameBuilder.Encode(new StorageHello(localFormat))
                : FrameBuilder.EncodeError(new QuicRemoteError(QuicRemoteError.StorageFormatMismatch, mismatch.Message));
            var sent = await QuicIo.WriteAsync(stream, Encode(response, mismatch is not null), true, scope);
            if (mismatch is not null)
            {
                await stream.WritesClosed.WaitAsync(scope.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            return sent switch
            {
                QuicSent => mismatch,
                QuicFault fault => mismatch ?? fault,
            };
        }
    }

    private static StorageFormatMismatch? Compare(string localFormat, string remoteFormat) =>
        string.Equals(localFormat, remoteFormat, StringComparison.Ordinal)
            ? null
            : new StorageFormatMismatch($"storage_format_mismatch: local storage format '{localFormat}' differs from peer storage format '{remoteFormat}'.");

    private static ReadOnlyMemory<byte> Encode(FrameBuilder frame, bool error = false) =>
        frame.Seal(QuicMessageType.StorageHello, error ? (byte)(QuicProtocol.ControlEndOfStream | QuicProtocol.ControlError) : QuicProtocol.ControlEndOfStream, 0);
}
