using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.TerraBase.Engine;

/// <summary>
/// Version 1 SHA-256 identity of a contiguous journal prefix. Lengths and integers are little-endian;
/// text is strict UTF-8, and a -1 body length distinguishes a deletion from an empty body.
/// </summary>
public static class JournalDigest
{
    /// <summary>The number of bytes in a prefix digest.</summary>
    public const int Length = 32;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The identity of the empty journal of this 16-byte database origin.</summary>
    public static byte[] Initial(ReadOnlySpan<byte> origin)
    {
        if (origin.Length != 16)
        {
            throw new InvalidDataException("a journal origin has 16 bytes");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("TerraBase/journal-prefix/v1\0"u8);
        hash.AppendData(origin);
        return hash.GetHashAndReset();
    }

    /// <summary>Extends the preceding prefix with one bounded, ordered group, including its origin and sequence.</summary>
    public static byte[] Compute(
        ReadOnlySpan<byte> origin, long sequence, ReadOnlySpan<byte> previous, IReadOnlyList<DocumentChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (origin.Length != 16 || sequence <= 0 || previous.Length != Length
            || changes.Count is 0 or > IStorageTransaction.MaxChangeGroupChanges)
        {
            throw new InvalidDataException("a journal digest requires a valid origin, sequence, preceding digest and bounded group");
        }

        long bytes = 0;
        foreach (var change in changes)
        {
            ArgumentNullException.ThrowIfNull(change);
            bytes += (long)Utf8.GetByteCount(change.Collection) + Utf8.GetByteCount(change.Id) + (change.Json?.Length ?? 0);
            if (bytes > IStorageTransaction.MaxChangeGroupBytes)
            {
                throw new InvalidDataException("the journal digest group exceeds the journal byte limit");
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("TerraBase/journal-group/v1\0"u8);
        hash.AppendData(origin);
        hash.AppendData(previous);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(number, sequence);
        hash.AppendData(number);
        BinaryPrimitives.WriteInt32LittleEndian(number, changes.Count);
        hash.AppendData(number[..4]);
        foreach (var change in changes)
        {
            AppendText(hash, change.Collection);
            AppendText(hash, change.Id);
            BinaryPrimitives.WriteInt64LittleEndian(number, change.Version);
            hash.AppendData(number);
            BinaryPrimitives.WriteInt32LittleEndian(number, change.Json?.Length ?? -1);
            hash.AppendData(number[..4]);
            if (change.Json is { } json)
            {
                hash.AppendData(json.Span);
            }
        }

        return hash.GetHashAndReset();
    }

    private static void AppendText(IncrementalHash hash, string text)
    {
        var bytes = Utf8.GetBytes(text);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
