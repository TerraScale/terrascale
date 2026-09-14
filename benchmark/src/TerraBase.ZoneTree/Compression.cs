using System.IO.Compression;

namespace TerraBase.ZoneTree;

/// <summary>Identifies a persisted compression format.</summary>
public enum CompressionCodecKind : byte
{
    /// <summary>Bytes are stored without transformation.</summary>
    None = 0,

    /// <summary>Bytes are encoded with Brotli.</summary>
    Brotli = 1,
}

/// <summary>Compresses and decompresses one bounded payload.</summary>
public interface ICompressionCodec
{
    /// <summary>The format identifier written alongside a payload.</summary>
    CompressionCodecKind Kind { get; }

    /// <summary>Compresses the source into an owned byte array.</summary>
    byte[] Compress(ReadOnlySpan<byte> source);

    /// <summary>
    /// Decompresses a payload with an explicit output limit.
    /// </summary>
    /// <param name="compressed">The encoded payload.</param>
    /// <param name="expectedLength">The original length recorded in metadata.</param>
    /// <param name="maximumLength">The largest permitted output.</param>
    byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedLength, int maximumLength = CompressionLimits.DefaultMaximumOutputLength);
}

/// <summary>Common output limits for compression codecs.</summary>
public static class CompressionLimits
{
    /// <summary>Default maximum decompressed block size (64 MiB).</summary>
    public const int DefaultMaximumOutputLength = 64 * 1024 * 1024;
}

/// <summary>A codec that copies bytes without compression.</summary>
public sealed class NoCompressionCodec : ICompressionCodec
{
    /// <summary>The shared stateless instance.</summary>
    public static NoCompressionCodec Instance { get; } = new();

    /// <inheritdoc />
    public CompressionCodecKind Kind => CompressionCodecKind.None;

    /// <inheritdoc />
    public byte[] Compress(ReadOnlySpan<byte> source) => source.ToArray();

    /// <inheritdoc />
    public byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedLength, int maximumLength = CompressionLimits.DefaultMaximumOutputLength)
    {
        ValidateLengths(expectedLength, maximumLength);
        if (compressed.Length != expectedLength)
        {
            throw new InvalidDataException("The uncompressed payload length does not match its metadata.");
        }

        return compressed.ToArray();
    }

    private static void ValidateLengths(int expectedLength, int maximumLength)
    {
        if (expectedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength), expectedLength, "The expected length cannot be negative.");
        }

        if (maximumLength < 0 || expectedLength > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength), maximumLength, "The decompressed payload exceeds the configured limit.");
        }
    }
}

/// <summary>A Brotli codec with bounded decompression.</summary>
public sealed class BrotliCompressionCodec : ICompressionCodec
{
    /// <summary>The shared codec using Brotli's balanced quality level.</summary>
    public static BrotliCompressionCodec Instance { get; } = new();

    private readonly int quality;
    private readonly int window;

    /// <summary>Creates a codec with the requested Brotli settings.</summary>
    /// <param name="quality">A Brotli quality in the inclusive range 0 through 11.</param>
    /// <param name="window">A Brotli window in the inclusive range 10 through 24.</param>
    public BrotliCompressionCodec(int quality = 4, int window = 22)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 11);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window, 24);
        this.quality = quality;
        this.window = window;
    }

    /// <inheritdoc />
    public CompressionCodecKind Kind => CompressionCodecKind.Brotli;

    /// <inheritdoc />
    public byte[] Compress(ReadOnlySpan<byte> source)
    {
        var capacity = BrotliEncoder.GetMaxCompressedLength(source.Length);
        var destination = new byte[capacity];
        if (!BrotliEncoder.TryCompress(source, destination, out var written, quality, window))
        {
            throw new InvalidDataException("Brotli could not compress the payload.");
        }

        return destination.AsSpan(0, written).ToArray();
    }

    /// <inheritdoc />
    public byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedLength, int maximumLength = CompressionLimits.DefaultMaximumOutputLength)
    {
        if (expectedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength), expectedLength, "The expected length cannot be negative.");
        }

        if (maximumLength < 0 || expectedLength > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength), maximumLength, "The decompressed payload exceeds the configured limit.");
        }

        var destination = new byte[expectedLength];
        if (!BrotliDecoder.TryDecompress(compressed, destination, out var written) || written != expectedLength)
        {
            throw new InvalidDataException("The Brotli payload is invalid or its metadata is incorrect.");
        }

        return destination;
    }
}

/// <summary>Factory for the codecs built into this project.</summary>
public static class CompressionCodecs
{
    /// <summary>Returns the no compression codec.</summary>
    public static ICompressionCodec None => NoCompressionCodec.Instance;

    /// <summary>Returns the shared Brotli codec.</summary>
    public static ICompressionCodec Brotli => BrotliCompressionCodec.Instance;

    /// <summary>Resolves a codec by its persisted identifier.</summary>
    public static ICompressionCodec Resolve(CompressionCodecKind kind) => kind switch
    {
        CompressionCodecKind.None => NoCompressionCodec.Instance,
        CompressionCodecKind.Brotli => BrotliCompressionCodec.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The compression codec is not supported."),
    };
}
