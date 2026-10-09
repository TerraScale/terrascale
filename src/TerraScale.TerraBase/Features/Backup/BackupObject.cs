using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NativeCompressions;

namespace TerraBase.Features.Backup;

/// <summary>
/// The stored form of every backup object, format 1 (<c>docs/backup.md</c>): a header, then chunks of at most 1 MiB, each
/// LZ4-compressed when that is smaller and sealed with AES-256-GCM.
/// </summary>
/// <remarks>
/// Header (62 bytes): magic <c>TBK1</c>, the 26-character id of the project data key, a random 32-byte salt. Each object
/// gets its own AES key, HKDF-SHA256 of the data key with the salt and, as info, the object's bucket key, so an object
/// moved to another key does not decrypt. Chunk <c>i</c>: a big-endian word with the plain length (low 30 bits), bit 30
/// set when LZ4-compressed and bit 31 on the last chunk; a big-endian word with the stored length; the ciphertext and its
/// 16-byte tag. The nonce is <c>i</c> (8 bytes, big-endian) after four zero bytes; the associated data is the header,
/// <c>i</c> and both words, so chunks cannot be reordered, dropped or cut off. An empty object is one empty last chunk.
/// </remarks>
internal static class BackupObject
{
    private const int KeyIdBytes = 26;
    private const int SaltBytes = 32;
    private const int HeaderBytes = 4 + KeyIdBytes + SaltBytes;
    private const int ChunkBytes = 1 << 20;
    private const int TagBytes = 16;
    private const uint LastChunk = 1u << 31;
    private const uint Compressed = 1u << 30;
    private const uint LengthMask = Compressed - 1;

    private static readonly int MaxStored = LZ4.Block.GetMaxCompressedLength(ChunkBytes);

    private static ReadOnlySpan<byte> Magic => "TBK1"u8;

    /// <summary>Seals <paramref name="plain"/> into <paramref name="output"/> as the object <paramref name="objectKey"/>.</summary>
    public static async Task WriteAsync(Stream plain, Stream output, string objectKey, string keyId, byte[] dataKey, CancellationToken cancellationToken)
    {
        if (keyId.Length != KeyIdBytes || !Ascii.IsValid(keyId))
        {
            throw new ArgumentException("a data key id is 26 ASCII characters", nameof(keyId));
        }

        var header = new byte[HeaderBytes];
        Magic.CopyTo(header);
        Encoding.ASCII.GetBytes(keyId, header.AsSpan(4));
        RandomNumberGenerator.Fill(header.AsSpan(4 + KeyIdBytes));
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        using var aes = new AesGcm(Derive(dataKey, header, objectKey), TagBytes);
        var input = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var packed = ArrayPool<byte>.Shared.Rent(MaxStored);
        var chunk = ArrayPool<byte>.Shared.Rent(8 + MaxStored + TagBytes);
        try
        {
            for (ulong index = 0; ; index++)
            {
                var read = await plain.ReadAtLeastAsync(input.AsMemory(0, ChunkBytes), ChunkBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                // A full chunk may be followed by nothing: an empty last chunk then ends the object.
                var last = read < ChunkBytes;
                var size = LZ4.Block.Compress(input.AsSpan(0, read), packed);
                var compressed = size < read;
                ReadOnlySpan<byte> stored = compressed ? packed.AsSpan(0, size) : input.AsSpan(0, read);
                BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)read | (compressed ? Compressed : 0) | (last ? LastChunk : 0));
                BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), (uint)stored.Length);
                aes.Encrypt(Nonce(index), stored, chunk.AsSpan(8, stored.Length), chunk.AsSpan(8 + stored.Length, TagBytes), Associated(header, index, chunk));
                await output.WriteAsync(chunk.AsMemory(0, 8 + stored.Length + TagBytes), cancellationToken).ConfigureAwait(false);
                if (last)
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(packed);
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// Opens the object <paramref name="objectKey"/> from <paramref name="sealedObject"/> into <paramref name="output"/>;
    /// <paramref name="dataKey"/> returns the data key with an id.
    /// </summary>
    /// <exception cref="InvalidDataException">The object is not format 1, truncated, or fails authentication.</exception>
    public static async Task ReadAsync(
        Stream sealedObject, Stream output, string objectKey, Func<string, Task<byte[]>> dataKey, CancellationToken cancellationToken,
        BackupReadObservation? observation = null)
    {
        var header = new byte[HeaderBytes];
        if (await ReadAsync(sealedObject, header, HeaderBytes, observation, cancellationToken).ConfigureAwait(false) < HeaderBytes
            || !header.AsSpan(0, 4).SequenceEqual(Magic) || !Ascii.IsValid(header.AsSpan(4, KeyIdBytes)))
        {
            throw new InvalidDataException("not a TerraBase backup object of format 1");
        }

        byte[] key;
        using (observation?.Enter(BackupReadObservation.Phase.Key))
        {
            key = await dataKey(Encoding.ASCII.GetString(header, 4, KeyIdBytes)).ConfigureAwait(false);
        }

        using var aes = OpenAes(key, header, objectKey, observation);
        var chunk = ArrayPool<byte>.Shared.Rent(8 + MaxStored + TagBytes);
        var plain = ArrayPool<byte>.Shared.Rent(MaxStored);
        var unpacked = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            for (ulong index = 0; ; index++)
            {
                if (await ReadAsync(sealedObject, chunk.AsMemory(0, 8), 8, observation, cancellationToken).ConfigureAwait(false) < 8)
                {
                    throw new InvalidDataException("backup object is truncated");
                }

                var flags = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                var length = (int)(flags & LengthMask);
                var storedLength = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(4));
                var compressed = (flags & Compressed) != 0;
                if (length > ChunkBytes || storedLength > MaxStored || (!compressed && storedLength != length))
                {
                    throw new InvalidDataException("backup object chunk is malformed");
                }

                var stored = (int)storedLength;
                if (await ReadAsync(sealedObject, chunk.AsMemory(8, stored + TagBytes), stored + TagBytes, observation, cancellationToken)
                        .ConfigureAwait(false) < stored + TagBytes)
                {
                    throw new InvalidDataException("backup object is truncated");
                }

                Memory<byte> data;
                using (observation?.Enter(BackupReadObservation.Phase.Decode))
                {
                    try
                    {
                        aes.Decrypt(Nonce(index), chunk.AsSpan(8, stored), chunk.AsSpan(8 + stored, TagBytes), plain.AsSpan(0, stored), Associated(header, index, chunk));
                    }
                    catch (AuthenticationTagMismatchException)
                    {
                        throw new InvalidDataException("backup object fails authentication");
                    }

                    data = plain.AsMemory(0, stored);
                    if (compressed)
                    {
                        try
                        {
                            if (LZ4.Block.Decompress(plain.AsSpan(0, stored), unpacked.AsSpan(0, length), null) != length)
                            {
                                throw new InvalidDataException("backup object chunk does not decompress to its length");
                            }
                        }
                        catch (LZ4Exception)
                        {
                            throw new InvalidDataException("backup object chunk is not LZ4");
                        }

                        data = unpacked.AsMemory(0, length);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                // A zero-byte write still allocates a FileStream buffer.
                if (!data.IsEmpty)
                {
                    using var write = observation?.Enter(BackupReadObservation.Phase.Output);
                    await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                }

                if ((flags & LastChunk) != 0)
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            ArrayPool<byte>.Shared.Return(plain);
            ArrayPool<byte>.Shared.Return(unpacked);
        }
    }

    private static AesGcm OpenAes(byte[] key, byte[] header, string objectKey, BackupReadObservation? observation)
    {
        using var decode = observation?.Enter(BackupReadObservation.Phase.Decode);
        return new AesGcm(Derive(key, header, objectKey), TagBytes);
    }

    private static ValueTask<int> ReadAsync(Stream source, Memory<byte> buffer, int minimum, BackupReadObservation? observation, CancellationToken cancellationToken) =>
        observation is null
            ? source.ReadAtLeastAsync(buffer, minimum, throwOnEndOfStream: false, cancellationToken)
            : ReadObservedAsync(source, buffer, minimum, observation, cancellationToken);

    private static async ValueTask<int> ReadObservedAsync(Stream source, Memory<byte> buffer, int minimum, BackupReadObservation observation, CancellationToken cancellationToken)
    {
        using var read = observation.Enter(BackupReadObservation.Phase.Read);
        return await source.ReadAtLeastAsync(buffer, minimum, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Derive(byte[] dataKey, byte[] header, string objectKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, header.AsSpan(4 + KeyIdBytes).ToArray(), Encoding.UTF8.GetBytes("terrabase backup 1\0" + objectKey));

    private static byte[] Nonce(ulong index)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    private static byte[] Associated(byte[] header, ulong index, byte[] chunk)
    {
        var associated = new byte[HeaderBytes + 16];
        header.CopyTo(associated, 0);
        BinaryPrimitives.WriteUInt64BigEndian(associated.AsSpan(HeaderBytes), index);
        chunk.AsSpan(0, 8).CopyTo(associated.AsSpan(HeaderBytes + 8));
        return associated;
    }
}
