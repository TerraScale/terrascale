using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NativeCompressions;

namespace TerraBase.Features.Backup;

internal static partial class BackupObject
{
    internal static async Task<string> SnapshotWrappedKeyAsync(Stream input, string objectKey, Func<string, Task<byte[]>> dataKey, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderBytes];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic) || !Ascii.IsValid(header.AsSpan(4, KeyIdBytes)))
        {
            throw new InvalidDataException("not a backup object of format 1");
        }
        var chunk = new byte[8 + MaxStored + TagBytes];
        await input.ReadExactlyAsync(chunk.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(chunk);
        var length = (int)(flags & LengthMask);
        var stored = BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(4));
        var compressed = (flags & Compressed) != 0;
        if (length > ChunkBytes || stored > MaxStored || (!compressed && stored != length))
        {
            throw new InvalidDataException("backup object chunk is malformed");
        }
        await input.ReadExactlyAsync(chunk.AsMemory(8, (int)stored + TagBytes), cancellationToken).ConfigureAwait(false);
        var key = await dataKey(Encoding.ASCII.GetString(header, 4, KeyIdBytes)).ConfigureAwait(false);
        using var aes = new AesGcm(Derive(key, header, objectKey), TagBytes);
        var plain = new byte[stored];
        try
        {
            aes.Decrypt(Nonce(0), chunk.AsSpan(8, (int)stored), chunk.AsSpan(8 + (int)stored, TagBytes), plain, Associated(header, 0, chunk));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("backup object fails authentication");
        }
        var bundle = plain;
        if (compressed)
        {
            bundle = new byte[length];
            try
            {
                if (LZ4.Block.Decompress(plain, bundle, null) != length)
                {
                    throw new InvalidDataException("backup object chunk does not decompress to its length");
                }
            }
            catch (LZ4Exception)
            {
                throw new InvalidDataException("backup object chunk is not LZ4");
            }
        }
        return SnapshotBundle.WrappedKey(bundle);
    }
}
