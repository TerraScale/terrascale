using System.Text;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Primitives;

namespace TerraBase.Features.Replication;

/// <summary>
/// The binary form of one client database change group, the plaintext inside an <see cref="ApplicationEnvelope"/> on a
/// <c>Replication.Sync</c> stream and in a WAN pull (docs/design/anna-core.md, Per-origin streams):
/// <c>"TBDG" || format:u8 (1) || dependencies || count || change[count]</c>, where the dependencies are
/// <see cref="OriginPositions"/>, the count is a minimal LEB128, and a change is
/// <c>collection_len || collection || id_len || id || state_len || state</c> with minimal LEB128 lengths, UTF-8 text and
/// the <see cref="LatticeEncoding"/> of the document's register delta. Nothing may follow the last change.
/// </summary>
public static class DeltaGroup
{
    /// <summary>The format <see cref="Encode"/> writes after the magic; decoding refuses any other.</summary>
    public const byte Format = 1;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> Magic => "TBDG"u8;

    /// <summary>
    /// Encodes a group of 1 to <see cref="IStorageTransaction.MaxChangeGroupChanges"/> changes; <paramref name="contentBytes"/>
    /// are its bytes of collections, ids and states, which the group limits bound.
    /// </summary>
    public static byte[] Encode(CapturedGroup group, out long contentBytes)
    {
        ArgumentNullException.ThrowIfNull(group);
        var changes = group.Changes.Select(change => (
            Collection: Utf8.GetBytes(change.Collection), Id: Utf8.GetBytes(change.Id), State: LatticeEncoding.Encode(change.State))).ToList();
        contentBytes = changes.Sum(change => (long)change.Collection.Length + change.Id.Length + change.State.Length);
        var size = Magic.Length + 1 + OriginPositions.Size(group.Dependencies.Count) + LengthOf(changes.Count)
            + changes.Sum(change => LengthOf(change.Collection.Length) + change.Collection.Length + LengthOf(change.Id.Length) + change.Id.Length
                + LengthOf(change.State.Length) + change.State.Length);
        var bytes = new byte[size];
        Magic.CopyTo(bytes);
        var at = Magic.Length;
        bytes[at++] = Format;
        at += OriginPositions.Write(group.Dependencies, bytes.AsSpan(at));
        at = WriteLength(bytes, at, changes.Count);
        foreach (var (collection, id, state) in changes)
        {
            foreach (var part in (ReadOnlySpan<byte[]>)[collection, id, state])
            {
                at = WriteLength(bytes, at, part.Length);
                part.CopyTo(bytes, at);
                at += part.Length;
            }
        }

        return at == bytes.Length ? bytes : throw new InvalidOperationException("the change group encoding was sized wrongly");
    }

    /// <summary>
    /// Decodes and validates a change group: 1 to <paramref name="maxChanges"/> changes of distinct documents, at most
    /// <paramref name="maxContentBytes"/> bytes of collections, ids and states (and at most
    /// <see cref="IStorageTransaction.MaxChangeGroupBytes"/> for several changes, as the engine applies them), valid collection names and document ids,
    /// canonical registers that hold a write, and JSON object bodies.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a valid change group.</exception>
    public static CapturedGroup Decode(
        ReadOnlySpan<byte> plain, int maxChanges = IStorageTransaction.MaxChangeGroupChanges, int maxContentBytes = IStorageTransaction.MaxSingleChangeBytes)
    {
        if (plain.Length < Magic.Length + 1 || !plain[..Magic.Length].SequenceEqual(Magic) || plain[Magic.Length] != Format)
        {
            throw new InvalidDataException("not a change group of this format");
        }

        var rest = plain[(Magic.Length + 1)..];
        var dependencies = OriginPositions.Read(rest, out var read);
        rest = rest[read..];
        var count = ReadLength(ref rest);
        if (count == 0 || count > maxChanges)
        {
            throw new InvalidDataException($"a change group holds 1 to {maxChanges} changes");
        }

        if (count > 1)
        {
            maxContentBytes = Math.Min(maxContentBytes, IStorageTransaction.MaxChangeGroupBytes);
        }

        var changes = new List<DocumentDelta>(count);
        var keys = new HashSet<(string, string)>();
        long content = 0;
        try
        {
            for (var index = 0; index < count; index++)
            {
                var collection = Take(ref rest);
                var id = Take(ref rest);
                var state = Take(ref rest);
                content += collection.Length + id.Length + state.Length;
                if (content > maxContentBytes)
                {
                    throw new InvalidDataException($"a change group holds at most {maxContentBytes} bytes of collections, ids and states");
                }

                var change = new DocumentDelta(Utf8.GetString(collection), Utf8.GetString(id), LatticeEncoding.DecodeDocument(state));
                if (!DocumentDelta.HasValidKey(change.Collection, change.Id) || change.State.Values.Count == 0 || !keys.Add((change.Collection, change.Id))
                    || change.State.Values.Values.Any(write => write.Stamp.Value > long.MaxValue))
                {
                    throw new InvalidDataException("a change has an invalid or repeated collection and id, holds no write, or a stamp beyond any version");
                }

                foreach (var write in change.State.Values.Values.Where(write => !write.IsTombstone))
                {
                    _ = DocumentJson.Read(write.Body.ToArray(), requireObject: true);
                }

                changes.Add(change);
            }
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("a change group holds invalid text");
        }
        catch (DocumentValidationException error)
        {
            throw new InvalidDataException("a change holds an inadmissible document", error);
        }

        return rest.IsEmpty ? new CapturedGroup(dependencies, changes) : throw new InvalidDataException("a change group has trailing bytes");
    }

    private static ReadOnlySpan<byte> Take(ref ReadOnlySpan<byte> rest)
    {
        var length = ReadLength(ref rest);
        if (length > rest.Length)
        {
            throw new InvalidDataException("a change group is truncated");
        }

        var taken = rest[..length];
        rest = rest[length..];
        return taken;
    }

    // A minimal LEB128 length of at most int.MaxValue.
    private static int ReadLength(ref ReadOnlySpan<byte> rest)
    {
        uint value = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (rest.IsEmpty)
            {
                throw new InvalidDataException("a change group is truncated");
            }

            var next = rest[0];
            rest = rest[1..];
            if (shift == 28 && next > 0x07)
            {
                throw new InvalidDataException("a change group holds a length beyond its limit");
            }

            value |= (uint)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return next == 0 && shift > 0 ? throw new InvalidDataException("a change group holds a length that is not minimal") : (int)value;
            }
        }
    }

    private static int WriteLength(byte[] bytes, int at, int value)
    {
        var rest = (uint)value;
        for (; rest >= 0x80; rest >>= 7)
        {
            bytes[at++] = (byte)(rest | 0x80);
        }

        bytes[at++] = (byte)rest;
        return at;
    }

    private static int LengthOf(int value)
    {
        var bytes = 1;
        for (var rest = (uint)value; rest >= 0x80; rest >>= 7)
        {
            bytes++;
        }

        return bytes;
    }
}
