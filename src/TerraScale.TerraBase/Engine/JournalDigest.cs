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
        if (changes.Count > IStorageTransaction.MaxChangeGroupChanges)
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

        using var group = new Group(origin, sequence, previous, changes.Count);
        foreach (var change in changes)
        {
            group.Append(change);
        }

        return group.Complete();
    }

    /// <summary>
    /// The same identity as <see cref="Compute"/>, built one change at a time for groups read or received in pages
    /// (the system database's groups exceed client group bounds). The caller bounds the group.
    /// </summary>
    public sealed class Group : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly int _count;
        private int _appended;

        /// <summary>Starts the group of <paramref name="count"/> changes after the prefix <paramref name="previous"/>.</summary>
        public Group(ReadOnlySpan<byte> origin, long sequence, ReadOnlySpan<byte> previous, int count)
        {
            if (origin.Length != 16 || sequence <= 0 || previous.Length != Length || count <= 0)
            {
                _hash.Dispose();
                throw new InvalidDataException("a journal digest requires a valid origin, sequence, preceding digest and bounded group");
            }

            _count = count;
            _hash.AppendData("TerraBase/journal-group/v1\0"u8);
            _hash.AppendData(origin);
            _hash.AppendData(previous);
            Span<byte> number = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(number, sequence);
            _hash.AppendData(number);
            BinaryPrimitives.WriteInt32LittleEndian(number, count);
            _hash.AppendData(number[..4]);
        }

        /// <summary>Appends the next change of the group.</summary>
        public void Append(DocumentChange change)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (_appended == _count)
            {
                throw new InvalidDataException("a journal group holds more changes than it declared");
            }

            _appended++;
            AppendText(_hash, change.Collection);
            AppendText(_hash, change.Id);
            Span<byte> number = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(number, change.Version);
            _hash.AppendData(number);
            BinaryPrimitives.WriteInt32LittleEndian(number, change.Json?.Length ?? -1);
            _hash.AppendData(number[..4]);
            if (change.Json is { } json)
            {
                _hash.AppendData(json.Span);
            }
        }

        /// <summary>The digest of the prefix through this group, once every declared change was appended.</summary>
        public byte[] Complete() =>
            _appended == _count ? _hash.GetHashAndReset() : throw new InvalidDataException("a journal group holds fewer changes than it declared");

        /// <inheritdoc />
        public void Dispose() => _hash.Dispose();
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
