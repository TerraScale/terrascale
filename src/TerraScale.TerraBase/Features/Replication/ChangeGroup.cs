using System.Buffers.Binary;
using System.Text;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Features.Replication;

/// <summary>
/// Version 1 binary form of a page of one system database change group, the plaintext inside an
/// <see cref="ApplicationEnvelope"/> of a catalog follower's stream (docs/decisions/0009; TS-647 step 5 replaces it). All
/// integers are little-endian:
/// <c>"TBDCG001" || count:u32 || change[count]</c>, where a change is
/// <c>kind:u8 (0 put, 1 delete) || collection_len:u16 || collection || id_len:u16 || id || version:u64</c>
/// followed, for a put, by <c>json_len:u32 || json</c>. Text is UTF-8; nothing may follow the last change.
/// </summary>
public static class ChangeGroup
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> Magic => "TBDCG001"u8;

    /// <summary>Encodes 1 to <see cref="IStorageTransaction.MaxChangeGroupChanges"/> changes.</summary>
    public static byte[] Encode(IReadOnlyList<DocumentChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        using var buffer = new MemoryStream();
        Span<byte> number = stackalloc byte[sizeof(ulong)];
        buffer.Write(Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)changes.Count);
        buffer.Write(number[..sizeof(uint)]);
        foreach (var change in changes)
        {
            buffer.WriteByte(change.Json is null ? (byte)1 : (byte)0);
            WriteText(buffer, change.Collection);
            WriteText(buffer, change.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(number, (ulong)change.Version);
            buffer.Write(number);
            if (change.Json is { } json)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)json.Length);
                buffer.Write(number[..sizeof(uint)]);
                buffer.Write(json.Span);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes and validates a page: 1 to <paramref name="maxChanges"/> changes, at most <paramref name="maxContentBytes"/>
    /// bytes of collections, ids and JSON (framing excluded), non-empty keys, positive versions and JSON values.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a valid page.</exception>
    public static List<DocumentChange> Decode(ReadOnlySpan<byte> plain, int maxChanges, int maxContentBytes)
    {
        if (plain.Length < Magic.Length + sizeof(uint) || !plain[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("not a version 1 change group");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(plain[Magic.Length..]);
        if (count == 0 || count > maxChanges)
        {
            throw new InvalidDataException($"a change group holds 1 to {maxChanges} changes");
        }

        var rest = plain[(Magic.Length + sizeof(uint))..];
        var changes = new List<DocumentChange>((int)count);
        var content = 0L;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var kind = Take(ref rest, 1)[0];
                var collection = Take(ref rest, BinaryPrimitives.ReadUInt16LittleEndian(Take(ref rest, sizeof(ushort))));
                var id = Take(ref rest, BinaryPrimitives.ReadUInt16LittleEndian(Take(ref rest, sizeof(ushort))));
                var version = BinaryPrimitives.ReadUInt64LittleEndian(Take(ref rest, sizeof(ulong)));
                var json = kind == 0 ? Take(ref rest, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(Take(ref rest, sizeof(uint))))) : default;
                content += collection.Length + id.Length + json.Length;
                if (content > maxContentBytes)
                {
                    throw new InvalidDataException($"a change group holds at most {maxContentBytes} bytes of collections, ids and documents");
                }

                changes.Add(Validate(kind, Utf8.GetString(collection), Utf8.GetString(id), version, json));
            }
        }
        catch (Exception error) when (error is DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("a change group holds invalid text or lengths");
        }

        return rest.IsEmpty ? changes : throw new InvalidDataException("a change group has trailing bytes");
    }

    private static DocumentChange Validate(byte kind, string collection, string id, ulong version, ReadOnlySpan<byte> json)
    {
        if (kind > 1 || version is 0 or > (ulong)VersionClock.MaxVersion || collection.Length == 0 || id.Length == 0)
        {
            throw new InvalidDataException("a change has an invalid kind, collection, id or version");
        }

        if (kind == 1)
        {
            return new DocumentChange(collection, id, (long)version, null);
        }

        var body = json.ToArray();
        try
        {
            _ = DocumentJson.Read(body, requireObject: false);
        }
        catch (DocumentValidationException error)
        {
            throw new InvalidDataException("a change holds an inadmissible document", error);
        }

        return new DocumentChange(collection, id, (long)version, body);
    }

    private static void WriteText(MemoryStream buffer, string text)
    {
        var bytes = Utf8.GetBytes(text);
        Span<byte> length = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)bytes.Length));
        buffer.Write(length);
        buffer.Write(bytes);
    }

    private static ReadOnlySpan<byte> Take(ref ReadOnlySpan<byte> rest, int length)
    {
        if (length > rest.Length)
        {
            throw new InvalidDataException("a change group is truncated");
        }

        var taken = rest[..length];
        rest = rest[length..];
        return taken;
    }

}
