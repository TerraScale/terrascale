namespace TerraScale.Auth.Crypto;

/// <summary>
/// Argon2id 1.3 (RFC 9106), written from scratch over <see cref="Blake2b"/>.
/// Sequential (lane-parallel semantics preserved; lanes are computed in the
/// canonical slice-then-lane order).
/// </summary>
public static class Argon2id
{
    private const int BlockBytes = 1024;
    private const int BlockWords = BlockBytes / sizeof(ulong);
    private const int SyncPoints = 4;
    private const int AddressesInBlock = BlockWords; // 128 pseudo-random words per address block
    private const int Version = 0x13;

    /// <summary>Derives a tag of <paramref name="tagSize"/> bytes.</summary>
    public static byte[] Derive(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        int memoryKiB,
        int passes,
        int lanes,
        int tagSize,
        ReadOnlySpan<byte> secret = default,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (lanes < 1) throw new ArgumentOutOfRangeException(nameof(lanes));
        if (passes < 1) throw new ArgumentOutOfRangeException(nameof(passes));
        if (tagSize < 4) throw new ArgumentOutOfRangeException(nameof(tagSize));
        if (memoryKiB < 8 * lanes) throw new ArgumentOutOfRangeException(nameof(memoryKiB), "Argon2 requires at least 8 KiB per lane");

        var segmentLength = memoryKiB / (SyncPoints * lanes);
        var laneLength = segmentLength * SyncPoints;
        var memoryBlocks = laneLength * lanes;

        // H0 per RFC 9106 section 3.2: parameters, then length-prefixed inputs.
        Span<byte> h0Input = stackalloc byte[24];
        WriteLE32(h0Input, lanes);
        WriteLE32(h0Input[4..], tagSize);
        WriteLE32(h0Input[8..], memoryKiB);
        WriteLE32(h0Input[12..], passes);
        WriteLE32(h0Input[16..], Version);
        WriteLE32(h0Input[20..], 2 /* Argon2id */);

        using var h0Stream = new MemoryStream();
        h0Stream.Write(h0Input.ToArray(), 0, h0Input.Length);
        AppendLengthPrefixed(h0Stream, password);
        AppendLengthPrefixed(h0Stream, salt);
        AppendLengthPrefixed(h0Stream, secret);
        AppendLengthPrefixed(h0Stream, associatedData);
        var h0 = Blake2b.Hash(64, h0Stream.ToArray());

        // Memory: lanes x laneLength blocks of 1024 bytes.
        var memory = new byte[lanes][];
        for (var lane = 0; lane < lanes; lane++)
        {
            memory[lane] = new byte[laneLength * BlockBytes];
        }

        Span<byte> firstBlockInput = stackalloc byte[64 + 8];
        for (var lane = 0; lane < lanes; lane++)
        {
            for (var column = 0; column < 2; column++)
            {
                h0.AsSpan().CopyTo(firstBlockInput);
                WriteLE32(firstBlockInput[64..], column);
                WriteLE32(firstBlockInput[68..], lane);
                VariableHash(firstBlockInput, memory[lane].AsSpan(column * BlockBytes, BlockBytes));
            }
        }

        var addressInput = new byte[BlockBytes];
        var addressBlock = new byte[BlockBytes];
        var zeroBlock = new byte[BlockBytes];
        var buffer = new byte[BlockBytes];

        for (var pass = 0; pass < passes; pass++)
        {
            for (var slice = 0; slice < SyncPoints; slice++)
            {
                for (var lane = 0; lane < lanes; lane++)
                {
                    var dataIndependent = pass == 0 && slice < SyncPoints / 2;
                    if (dataIndependent)
                    {
                        addressInput.AsSpan().Clear();
                        WriteUInt64(addressInput, 0, (ulong)pass);
                        WriteUInt64(addressInput, 8, (ulong)lane);
                        WriteUInt64(addressInput, 16, (ulong)slice);
                        WriteUInt64(addressInput, 24, (ulong)memoryBlocks);
                        WriteUInt64(addressInput, 32, (ulong)passes);
                        WriteUInt64(addressInput, 40, 2 /* Argon2id */);
                    }

                    var startingIndex = pass == 0 && slice == 0 ? 2 : 0;
                    if (startingIndex == 2 && dataIndependent)
                        NextAddresses(addressInput, addressBlock, zeroBlock);

                    for (var index = startingIndex; index < segmentLength; index++)
                    {
                        var absolute = slice * segmentLength + index;
                        var previous = PreviousPosition(laneLength, absolute);

                        ulong pseudoRandom;
                        if (dataIndependent)
                        {
                            if (index % AddressesInBlock == 0)
                                NextAddresses(addressInput, addressBlock, zeroBlock);
                            pseudoRandom = ReadUInt64(addressBlock, (index % AddressesInBlock) * 8);
                        }
                        else
                        {
                            pseudoRandom = ReadUInt64(memory[lane], previous * BlockBytes);
                        }

                        var referenceLane = (int)((pseudoRandom >> 32) % (ulong)lanes);
                        if (pass == 0 && slice == 0)
                            referenceLane = lane;

                        var referenceIndex = IndexAlpha(
                            pseudoRandom & 0xFFFFFFFFUL,
                            memoryBlocks: memoryBlocks,
                            laneLength: laneLength,
                            segmentLength: segmentLength,
                            pass: pass,
                            slice: slice,
                            lane: lane,
                            index: index,
                            sameLane: referenceLane == lane);

                        var currentSpan = memory[lane].AsSpan(absolute * BlockBytes, BlockBytes);
                        var previousSpan = memory[lane].AsSpan(previous * BlockBytes, BlockBytes);
                        var referenceSpan = memory[referenceLane].AsSpan(referenceIndex * BlockBytes, BlockBytes);

                        CompressBlocks(previousSpan, referenceSpan, currentSpan, buffer, xorIntoCurrent: pass > 0);
                    }
                }
            }
        }

        // C = XOR of the last block of every lane; Tag = H'^tagSize(C).
        var finalBlock = new byte[BlockBytes];
        for (var lane = 0; lane < lanes; lane++)
            XorInto(finalBlock, memory[lane].AsSpan((laneLength - 1) * BlockBytes, BlockBytes));

        var tag = new byte[tagSize];
        VariableHash(finalBlock, tag);
        return tag;
    }

    /// <summary>Variable-length hash H' (RFC 9106 section 3.2).</summary>
    private static void VariableHash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length <= 64)
        {
            Span<byte> smallHead = stackalloc byte[4 + input.Length];
            WriteLE32(smallHead, output.Length);
            input.CopyTo(smallHead[4..]);
            var digest = Blake2b.Hash(output.Length, smallHead);
            digest.CopyTo(output);
            return;
        }

        var tagSize = output.Length;
        var headedInput = new byte[4 + input.Length];
        WriteLE32(headedInput, tagSize);
        input.CopyTo(headedInput.AsSpan(4));
        var v1 = Blake2b.Hash(64, headedInput);
        v1.AsSpan(0, 32).CopyTo(output);

        var previous = v1;
        var emitted = 32;
        var chunk = new byte[64];
        while (true)
        {
            previous.CopyTo(chunk);
            var remaining = tagSize - emitted;
            if (remaining <= 64)
            {
                // The final digest is produced at exactly `remaining` bytes; BLAKE2b
                // digests of different lengths are unrelated, so it cannot be truncated.
                var last = Blake2b.Hash(remaining, chunk);
                last.CopyTo(output[emitted..]);
                return;
            }
            var next = Blake2b.Hash(64, chunk);
            next.AsSpan(0, 32).CopyTo(output[emitted..]);
            emitted += 32;
            previous = next;
        }
    }

    private static void NextAddresses(byte[] addressInput, byte[] addressBlock, byte[] zeroBlock)
    {
        var counter = ReadUInt64(addressInput, 48) + 1;
        WriteUInt64(addressInput, 48, counter);

        CompressBlocks(zeroBlock, addressInput, addressBlock, new byte[BlockBytes], xorIntoCurrent: false);
        CompressBlocks(zeroBlock, addressBlock, addressBlock, new byte[BlockBytes], xorIntoCurrent: false);
    }

    /// <summary>G(X, Y) written into <paramref name="buffer"/>, then stored (XORed on later passes) into the current block.</summary>
    private static void CompressBlocks(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, Span<byte> current, byte[] buffer, bool xorIntoCurrent)
    {
        // R = X XOR Y, kept intact for the final XOR.
        var r = new ulong[BlockWords];
        for (var i = 0; i < BlockWords; i++)
            r[i] = ReadUInt64(x, i * 8) ^ ReadUInt64(y, i * 8);

        // Q: row-wise P over each row of eight 16-byte registers.
        var q = new ulong[BlockWords];
        r.AsSpan().CopyTo(q);
        for (var row = 0; row < 8; row++)
            PermuteP(q.AsSpan(row * 16, 16));

        // Z: column-wise P over each column of eight registers.
        var z = new ulong[BlockWords];
        Span<ulong> registers = stackalloc ulong[16];
        for (var column = 0; column < 8; column++)
        {
            for (var row = 0; row < 8; row++)
                q.AsSpan(row * 16 + column * 2, 2).CopyTo(registers.Slice(row * 2, 2));
            PermuteP(registers);
            for (var row = 0; row < 8; row++)
                registers.Slice(row * 2, 2).CopyTo(z.AsSpan(row * 16 + column * 2, 2));
        }

        // Output Z XOR R.
        for (var i = 0; i < BlockWords; i++)
            WriteUInt64(buffer, i * 8, z[i] ^ r[i]);

        if (xorIntoCurrent)
        {
            for (var i = 0; i < BlockBytes; i++)
                current[i] ^= buffer[i];
        }
        else
        {
            buffer.AsSpan(0, BlockBytes).CopyTo(current);
        }
    }

    /// <summary>Permutation P over one row/column of eight 16-byte registers (128 bytes, 16 words).</summary>
    private static void PermuteP(Span<ulong> v)
    {
        Gb(v, 0, 4, 8, 12);
        Gb(v, 1, 5, 9, 13);
        Gb(v, 2, 6, 10, 14);
        Gb(v, 3, 7, 11, 15);
        Gb(v, 0, 5, 10, 15);
        Gb(v, 1, 6, 11, 12);
        Gb(v, 2, 7, 8, 13);
        Gb(v, 3, 4, 9, 14);
    }

    private static void Gb(Span<ulong> v, int a, int b, int c, int d)
    {
        v[a] = v[a] + v[b] + 2 * (v[a] & 0xFFFFFFFF) * (v[b] & 0xFFFFFFFF);
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d] + 2 * (v[c] & 0xFFFFFFFF) * (v[d] & 0xFFFFFFFF);
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + 2 * (v[a] & 0xFFFFFFFF) * (v[b] & 0xFFFFFFFF);
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d] + 2 * (v[c] & 0xFFFFFFFF) * (v[d] & 0xFFFFFFFF);
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }

    private static int PreviousPosition(int laneLength, int absolute)
    {
        if (absolute % laneLength == 0)
            return absolute + laneLength - 1;
        return absolute - 1;
    }

    private static int IndexAlpha(
        ulong pseudoRandomLow32,
        int memoryBlocks,
        int laneLength,
        int segmentLength,
        int pass,
        int slice,
        int lane,
        int index,
        bool sameLane)
    {
        int referenceAreaSize;
        if (pass == 0)
        {
            if (slice == 0)
                referenceAreaSize = index - 1;
            else if (sameLane)
                referenceAreaSize = slice * segmentLength + index - 1;
            else
                referenceAreaSize = slice * segmentLength + (index == 0 ? -1 : 0);
        }
        else
        {
            if (sameLane)
                referenceAreaSize = laneLength - segmentLength + index - 1;
            else
                referenceAreaSize = laneLength - segmentLength + (index == 0 ? -1 : 0);
        }

        var j1 = pseudoRandomLow32;
        var squareHigh = j1 * j1 >> 32;
        var scaled = (ulong)referenceAreaSize * squareHigh >> 32;
        var relativePosition = (uint)referenceAreaSize - 1 - (uint)scaled;

        var startPosition = 0;
        if (pass > 0)
            startPosition = slice == SyncPoints - 1 ? 0 : (slice + 1) * segmentLength;

        return (int)((ulong)(startPosition + relativePosition) % (ulong)laneLength);
    }

    private static void XorInto(byte[] target, ReadOnlySpan<byte> source)
    {
        for (var i = 0; i < target.Length; i++)
            target[i] ^= source[i];
    }

    private static void AppendLengthPrefixed(MemoryStream stream, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteLE32(length, data.Length);
        stream.Write(length.ToArray(), 0, 4);
        if (data.Length > 0)
        {
            var copy = data.ToArray();
            stream.Write(copy, 0, copy.Length);
        }
    }

    private static void WriteLE32(Span<byte> target, int value) => BinaryPrimitives.WriteInt32LittleEndian(target, value);

    private static void WriteUInt64(Span<byte> target, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(offset, 8), value);

    private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8));
}
