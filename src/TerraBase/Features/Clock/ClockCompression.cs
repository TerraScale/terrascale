using System.IO.Compression;
using Grpc.Net.Compression;
using K4os.Compression.LZ4.Streams;

namespace TerraBase.Features.Clock;

internal sealed class ClockCompression : ICompressionProvider
{
    public string EncodingName => "lz4";

    public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel) => LZ4Stream.Encode(stream, leaveOpen: true);

    public Stream CreateDecompressionStream(Stream stream) => LZ4Stream.Decode(stream, leaveOpen: true);
}
