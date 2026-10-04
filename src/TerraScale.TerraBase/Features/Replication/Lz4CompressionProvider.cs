using System.IO.Compression;
using Grpc.Net.Compression;
using NativeCompressions;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>
/// <c>lz4</c> gRPC message encoding (one LZ4 frame per message, content size in the frame header)
/// for inter-node traffic. Register it on both the peer server and every peer client; see
/// <c>docs/decisions/0001-grpc-lz4-transport-compression.md</c>.
/// </summary>
/// <remarks>
/// Uses the one-shot <see cref="LZ4"/> API: <c>LZ4Stream</c> in NativeCompressions 0.6.1 omits
/// the frame header and cannot decode its own output.
/// </remarks>
/// <param name="maxDecodedBytes">Largest decoded message accepted; larger frames fail instead of inflating.</param>
public sealed class Lz4CompressionProvider(int maxDecodedBytes) : ICompressionProvider
{
    /// <summary>The <c>grpc-encoding</c> name.</summary>
    public const string Name = "lz4";

    /// <inheritdoc />
    public string EncodingName => Name;

    /// <inheritdoc />
    public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel) => new Encoder(stream);

    /// <inheritdoc />
    public Stream CreateDecompressionStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // gRPC hands over one complete, size-checked message, already buffered in memory.
        using var encoded = new MemoryStream();
        stream.CopyTo(encoded);
        var frame = encoded.GetBuffer().AsSpan(0, (int)encoded.Length);

        // The declared size bounds the allocation; the decoder fails if the frame decodes to more.
        if (!LZ4.TryGetFrameInfo(frame, out var info) || info.ContentSize > (ulong)maxDecodedBytes)
        {
            throw new InvalidDataException($"lz4 frames must declare a content size of at most {maxDecodedBytes} bytes");
        }

        var decoded = new byte[info.ContentSize];
        var length = LZ4.Decompress(frame, decoded);
        return length == decoded.Length
            ? new MemoryStream(decoded, writable: false)
            : throw new InvalidDataException("lz4 frame decoded to a different size than it declared");
    }

    // Buffers one message and writes it to the transport as a single LZ4 frame on dispose.
    private sealed class Encoder(Stream destination) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing && CanRead)
            {
                var options = LZ4CompressionOptions.Default with { ContentSize = (ulong)Length };
                destination.Write(LZ4.Compress(GetBuffer().AsSpan(0, (int)Length), options));
            }

            base.Dispose(disposing);
        }
    }
}
