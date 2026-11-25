using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Platform.Features.Shapes;

public readonly union OpenShapeChunkOutcome(OpenedShapeChunk, PlatformUnavailable);

public sealed record OpenedShapeChunk(ShapeChunk Chunk, long PlainBytes);

public sealed record SealedChunk(byte[] Bytes, long Epoch, string Sha256, long PlainBytes);

public sealed class ShapeCrypto(PlatformOptions options, TimeProvider timeProvider)
{
    public const long EpochSeconds = 600;
    public const long RetainedEpochs = 144;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HeaderBytes = 4 + 1 + 16 + 8 + NonceBytes;
    private static readonly byte[] Magic = "TBS1"u8.ToArray();

    public long CurrentEpoch => timeProvider.GetUtcNow().ToUnixTimeSeconds() / EpochSeconds;

    public DateTimeOffset EpochEnd(long epoch) => DateTimeOffset.FromUnixTimeSeconds((epoch + 1) * EpochSeconds);

    public byte[] Key(string handle, long epoch, Ulid keyId)
    {
        var key = new byte[KeyBytes];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            options.MasterKey,
            key,
            Encoding.UTF8.GetBytes("TBS1\0" + handle),
            Encoding.UTF8.GetBytes("epoch:" + epoch + "\0key:" + keyId));
        return key;
    }

    public SealedChunk Seal(string handle, Ulid keyId, ShapeChunk chunk)
    {
        var epoch = CurrentEpoch;
        var json = JsonSerializer.SerializeToUtf8Bytes(chunk, ShapesJsonContext.Default.ShapeChunk);
        var plain = Compress(json);
        var output = new byte[HeaderBytes + plain.Length + TagBytes];
        Magic.CopyTo(output, 0);
        output[4] = 1;
        keyId.TryWriteBytes(output.AsSpan(5, 16));
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(21, 8), epoch);
        var nonce = output.AsSpan(29, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var key = Key(handle, epoch, keyId);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plain, output.AsSpan(HeaderBytes, plain.Length), output.AsSpan(HeaderBytes + plain.Length, TagBytes), Encoding.UTF8.GetBytes(handle));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new SealedChunk(output, epoch, Convert.ToHexStringLower(SHA256.HashData(output)), json.LongLength);
    }

    public OpenShapeChunkOutcome Open(string handle, ReadOnlySpan<byte> sealedChunk)
    {
        if (sealedChunk.Length < HeaderBytes + TagBytes || !sealedChunk[..4].SequenceEqual(Magic) || sealedChunk[4] != 1)
        {
            return new PlatformUnavailable("a sealed shape chunk is malformed", "shape_chunk_unreadable");
        }

        var keyId = new Ulid(sealedChunk.Slice(5, 16));
        var epoch = BinaryPrimitives.ReadInt64LittleEndian(sealedChunk.Slice(21, 8));
        var cipherLength = sealedChunk.Length - HeaderBytes - TagBytes;
        var plain = new byte[cipherLength];
        var key = Key(handle, epoch, keyId);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(sealedChunk.Slice(29, NonceBytes), sealedChunk.Slice(HeaderBytes, cipherLength), sealedChunk.Slice(HeaderBytes + cipherLength, TagBytes), plain,
                Encoding.UTF8.GetBytes(handle));
        }
        catch (AuthenticationTagMismatchException)
        {
            return new PlatformUnavailable("a sealed shape chunk is not authentic", "shape_chunk_unreadable");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return Decompress(plain) switch
        {
            byte[] json => TerraBaseAnswers.Parse(json, ShapesJsonContext.Default.ShapeChunk) is { } chunk
                ? new OpenedShapeChunk(chunk, json.LongLength)
                : new PlatformUnavailable("a sealed shape chunk holds unreadable JSON", "shape_chunk_unreadable"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static byte[] Compress(byte[] json)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(json);
        }

        return output.ToArray();
    }

    private readonly union DecompressShapeChunkOutcome(byte[], PlatformUnavailable);

    private static DecompressShapeChunkOutcome Decompress(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return new PlatformUnavailable("a sealed shape chunk is not valid Brotli", "shape_chunk_unreadable");
        }
    }
}
