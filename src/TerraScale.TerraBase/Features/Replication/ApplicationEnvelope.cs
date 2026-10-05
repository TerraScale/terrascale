using System.Buffers.Binary;
using System.Numerics;
using NativeCompressions;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>
/// The versioned <c>TBLZ4E01</c> application envelope of terrabase-rust (<c>tb-format/src/envelope.rs</c>):
/// a 40-byte little-endian header, then one raw LZ4 block. The header holds the magic, major version 1,
/// zero flags, the codec, the total, stored and decoded lengths and the CRC32C of the header, of the
/// stored block and of the decoded bytes. TerraBase peers write and accept only the LZ4 codec.
/// </summary>
public static class ApplicationEnvelope
{
    /// <summary>Header length in bytes.</summary>
    public const int HeaderBytes = 40;

    private const ushort Major = 1;
    private const byte Lz4Codec = 1;

    private static ReadOnlySpan<byte> Magic => "TBLZ4E01"u8;

    /// <summary>Compresses <paramref name="plain"/> (at least one byte) into an envelope.</summary>
    public static ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> plain)
    {
        if (plain.IsEmpty)
        {
            throw new ArgumentException("an envelope holds at least one byte", nameof(plain));
        }

        var envelope = new byte[HeaderBytes + LZ4.Block.GetMaxCompressedLength(plain.Length)];
        var stored = LZ4.Block.Compress(plain, envelope.AsSpan(HeaderBytes));
        var header = envelope.AsSpan(0, HeaderBytes);
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], Major);
        header[12] = Lz4Codec;
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)(HeaderBytes + stored));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)stored);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)plain.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], Crc32C(header[..28]));
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], Crc32C(envelope.AsSpan(HeaderBytes, stored)));
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], Crc32C(plain));
        return envelope.AsMemory(0, HeaderBytes + stored);
    }

    /// <summary>
    /// Validates an envelope and decompresses it into a buffer of exactly its declared size, which must
    /// be at most <paramref name="maxDecodedBytes"/>. Lengths and the header and block checksums are
    /// checked before anything is allocated.
    /// </summary>
    /// <exception cref="InvalidDataException">The envelope is truncated, oversized, corrupt or not LZ4.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> envelope, int maxDecodedBytes)
    {
        if (envelope.Length < HeaderBytes || !envelope[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("not an application envelope");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(envelope[8..]) != Major
            || BinaryPrimitives.ReadUInt16LittleEndian(envelope[10..]) != 0
            || envelope[12] != Lz4Codec
            || envelope[13..16].ContainsAnyExcept((byte)0))
        {
            throw new InvalidDataException("unsupported envelope version, flags or codec");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(envelope[28..]) != Crc32C(envelope[..28]))
        {
            throw new InvalidDataException("envelope header checksum mismatch");
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(envelope[16..]);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(envelope[20..]);
        var plain = BinaryPrimitives.ReadUInt32LittleEndian(envelope[24..]);
        if (total != envelope.Length || (long)total != HeaderBytes + (long)stored)
        {
            throw new InvalidDataException("envelope lengths are inconsistent");
        }

        if (plain == 0 || plain > maxDecodedBytes)
        {
            throw new InvalidDataException($"envelopes decode to 1 to {maxDecodedBytes} bytes");
        }

        var block = envelope[HeaderBytes..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(envelope[32..]) != Crc32C(block))
        {
            throw new InvalidDataException("envelope payload checksum mismatch");
        }

        var decoded = new byte[plain];
        int written;
        try
        {
            written = LZ4.Block.Decompress(block, decoded, null);
        }
        catch (LZ4Exception)
        {
            throw new InvalidDataException("envelope payload is not a valid LZ4 block of its declared size");
        }

        return written == decoded.Length && BinaryPrimitives.ReadUInt32LittleEndian(envelope[36..]) == Crc32C(decoded)
            ? decoded
            : throw new InvalidDataException("decoded envelope does not match its declared length and checksum");
    }

    // CRC-32C (Castagnoli) as in the Rust crc32c crate: initial and final value all ones.
    private static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        for (; data.Length >= sizeof(ulong); data = data[sizeof(ulong)..])
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
        }

        foreach (var value in data)
        {
            crc = BitOperations.Crc32C(crc, value);
        }

        return ~crc;
    }
}
