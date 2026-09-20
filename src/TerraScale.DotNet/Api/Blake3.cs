namespace TerraScale.DotNet;

/// <summary>Small unkeyed BLAKE3 implementation for artifact integrity checks.</summary>
internal static class Blake3
{
    private const uint ChunkStart = 1;
    private const uint ChunkEnd = 2;
    private const uint Parent = 4;
    private const uint Root = 8;
    private static readonly uint[] Iv = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
    private static readonly int[] Permutation = [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8];

    public static string HashHex(ReadOnlySpan<byte> input) => Convert.ToHexString(Hash(input)).ToLowerInvariant();

    public static async Task<string> HashHexAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var stack = new List<uint[]>();
        Output current = default;
        var chunk = new byte[1024];
        var index = 0UL;
        var seen = false;
        while (true)
        {
            var length = 0;
            while (length < chunk.Length)
            {
                var read = await input.ReadAsync(chunk.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length == 0) break;
            seen = true;
            current = AddChunk(stack, chunk.AsSpan(0, length), index++);
        }

        if (!seen) current = AddChunk(stack, ReadOnlySpan<byte>.Empty, 0);
        stack.RemoveAt(stack.Count - 1);
        while (stack.Count > 0)
        {
            current = ParentOutput(stack[^1], current.Cv());
            stack.RemoveAt(stack.Count - 1);
        }
        return Convert.ToHexString(RootHash(current)).ToLowerInvariant();
    }

    private static byte[] Hash(ReadOnlySpan<byte> input)
    {
        var stack = new List<uint[]>();
        Output current = default;
        var index = 0;
        for (var offset = 0; offset < input.Length || input.Length == 0 && index == 0; index++, offset += 1024)
        {
            var end = Math.Min(offset + 1024, input.Length);
            current = ChunkOutput(input[offset..end], (ulong)index);
            var cv = current.Cv();
            var total = index + 1;
            while ((total & 1) == 0)
            {
                current = ParentOutput(stack[^1], cv);
                stack.RemoveAt(stack.Count - 1);
                cv = current.Cv();
                total >>= 1;
            }
            stack.Add(cv);
        }

        stack.RemoveAt(stack.Count - 1);
        while (stack.Count > 0)
        {
            current = ParentOutput(stack[^1], current.Cv());
            stack.RemoveAt(stack.Count - 1);
        }

        return RootHash(current);
    }

    private static Output AddChunk(List<uint[]> stack, ReadOnlySpan<byte> chunk, ulong index)
    {
        var current = ChunkOutput(chunk, index);
        var cv = current.Cv();
        var total = index + 1;
        while ((total & 1) == 0)
        {
            current = ParentOutput(stack[^1], cv);
            stack.RemoveAt(stack.Count - 1);
            cv = current.Cv();
            total >>= 1;
        }
        stack.Add(cv);
        return current;
    }

    private static byte[] RootHash(Output current)
    {
        var words = Compress(current.CvWords, current.Words, 0, current.Length, current.Flags | Root);
        var result = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            result[i * 4] = (byte)words[i];
            result[i * 4 + 1] = (byte)(words[i] >> 8);
            result[i * 4 + 2] = (byte)(words[i] >> 16);
            result[i * 4 + 3] = (byte)(words[i] >> 24);
        }
        return result;
    }

    private readonly record struct Output(uint[] CvWords, uint[] Words, ulong Counter, uint Length, uint Flags)
    {
        public uint[] Cv() => Compress(CvWords, Words, Counter, Length, Flags)[..8];
    }

    private static Output ChunkOutput(ReadOnlySpan<byte> chunk, ulong index)
    {
        var cv = (uint[])Iv.Clone();
        var blocks = Math.Max(1, (chunk.Length + 63) / 64);
        for (var block = 0; block < blocks - 1; block++)
        {
            var words = WordsOf(chunk.Slice(block * 64, 64));
            var compressed = Compress(cv, words, index, 64, block == 0 ? ChunkStart : 0);
            Array.Copy(compressed, cv, 8);
        }

        var finalStart = (blocks - 1) * 64;
        var flags = ChunkEnd | (blocks == 1 ? ChunkStart : 0);
        return new Output(cv, WordsOf(chunk[finalStart..]), index, (uint)(chunk.Length - finalStart), flags);
    }

    private static Output ParentOutput(uint[] left, uint[] right)
    {
        var words = new uint[16];
        Array.Copy(left, words, 8);
        Array.Copy(right, 0, words, 8, 8);
        return new Output((uint[])Iv.Clone(), words, 0, 64, Parent);
    }

    private static uint[] WordsOf(ReadOnlySpan<byte> block)
    {
        var words = new uint[16];
        for (var i = 0; i < block.Length; i++) words[i / 4] |= (uint)block[i] << (8 * (i % 4));
        return words;
    }

    private static uint[] Compress(uint[] cv, uint[] words, ulong counter, uint length, uint flags)
    {
        Span<uint> state = stackalloc uint[16];
        cv.AsSpan().CopyTo(state);
        Iv.AsSpan().CopyTo(state[8..]);
        state[12] = (uint)counter;
        state[13] = (uint)(counter >> 32);
        state[14] = length;
        state[15] = flags;
        var message = (uint[])words.Clone();
        for (var round = 0; round < 7; round++)
        {
            G(ref state, 0, 4, 8, 12, message[0], message[1]);
            G(ref state, 1, 5, 9, 13, message[2], message[3]);
            G(ref state, 2, 6, 10, 14, message[4], message[5]);
            G(ref state, 3, 7, 11, 15, message[6], message[7]);
            G(ref state, 0, 5, 10, 15, message[8], message[9]);
            G(ref state, 1, 6, 11, 12, message[10], message[11]);
            G(ref state, 2, 7, 8, 13, message[12], message[13]);
            G(ref state, 3, 4, 9, 14, message[14], message[15]);
            var next = new uint[16];
            for (var i = 0; i < 16; i++) next[i] = message[Permutation[i]];
            message = next;
        }
        var result = state.ToArray();
        for (var i = 0; i < 8; i++)
        {
            result[i] ^= result[i + 8];
            result[i + 8] ^= cv[i];
        }
        return result;
    }

    private static void G(ref Span<uint> state, int a, int b, int c, int d, uint x, uint y)
    {
        state[a] += state[b] + x;
        state[d] = RotateRight(state[d] ^ state[a], 16);
        state[c] += state[d];
        state[b] = RotateRight(state[b] ^ state[c], 12);
        state[a] += state[b] + y;
        state[d] = RotateRight(state[d] ^ state[a], 8);
        state[c] += state[d];
        state[b] = RotateRight(state[b] ^ state[c], 7);
    }

    private static uint RotateRight(uint value, int amount) => value >> amount | value << (32 - amount);

}
