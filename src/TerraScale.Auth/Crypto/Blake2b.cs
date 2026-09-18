namespace TerraScale.Auth.Crypto;

/// <summary>
/// BLAKE2b (RFC 7693), written from scratch. Unkeyed and keyed hashing with
/// any digest size from 1 to 64 bytes. Not thread-safe; create one instance
/// per computation.
/// </summary>
public sealed class Blake2b
{
    private const int BlockSize = 128;
    private static readonly ulong[] Iv =
    [
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    ];

    private static readonly byte[] Sigma =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];

    private readonly ulong[] h = new ulong[8];
    private readonly byte[] buffer = new byte[BlockSize];
    private int bufferLength;
    private ulong counter;

    /// <summary>Creates a hasher with digest size <paramref name="digestSize"/> (1..64 bytes) and an optional key.</summary>
    public Blake2b(int digestSize, ReadOnlySpan<byte> key = default)
    {
        if (digestSize is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(digestSize));
        if (key.Length > 64) throw new ArgumentOutOfRangeException(nameof(key));
        DigestSize = digestSize;

        Iv.AsSpan().CopyTo(h);
        h[0] ^= 0x0101_0000u ^ (ulong)(key.Length << 8) ^ (ulong)digestSize;

        if (key.Length > 0)
        {
            key.CopyTo(buffer);
            bufferLength = BlockSize; // the padded key block is compressed like any full block
        }
    }

    /// <summary>Digest size in bytes.</summary>
    public int DigestSize { get; }

    /// <summary>Absorbs data. May be called repeatedly.</summary>
    public Blake2b Update(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (bufferLength == BlockSize)
            {
                counter += BlockSize;
                Compress(buffer, counter, lastBlock: false);
                bufferLength = 0;
            }
            var take = Math.Min(BlockSize - bufferLength, data.Length - offset);
            data.Slice(offset, take).CopyTo(buffer.AsSpan(bufferLength));
            bufferLength += take;
            offset += take;
        }
        return this;
    }

    /// <summary>Finishes the computation and returns the digest.</summary>
    public byte[] Final()
    {
        buffer.AsSpan(bufferLength).Clear();
        var t = counter + (ulong)bufferLength;
        Compress(buffer, t, lastBlock: true);

        var full = new byte[64];
        for (var i = 0; i < 8; i++)
            BitConverter.TryWriteBytes(full.AsSpan(i * 8), h[i]);
        return full[..DigestSize];
    }

    /// <summary>One-shot helper.</summary>
    public static byte[] Hash(int digestSize, ReadOnlySpan<byte> data, ReadOnlySpan<byte> key = default)
    {
        var blake = new Blake2b(digestSize, key);
        blake.Update(data);
        return blake.Final();
    }

    private void Compress(ReadOnlySpan<byte> block, ulong t, bool lastBlock)
    {
        var m = new ulong[16];
        for (var i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));

        var v = new ulong[16];
        h.CopyTo(v.AsSpan(0, 8));
        Iv.CopyTo(v.AsSpan(8, 8));

        v[12] ^= t;
        if (lastBlock)
            v[14] ^= ulong.MaxValue;

        for (var round = 0; round < 12; round++)
        {
            var s = Sigma.AsSpan((round % 10) * 16);
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }

        for (var i = 0; i < 8; i++)
            h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(ulong[] v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}
