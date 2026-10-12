using System.Buffers.Binary;
using System.Buffers.Text;
using TerraBase.Engine.Lattices;

namespace TerraBase.Features.Documents;

/// <summary>
/// The changes feed cursor (docs/terrabase/multi-region-replication.md section 14): per origin node, the highest stamp
/// delivered, valid on any machine. Opaque to clients: base64url of a format byte (1) and, per origin in ascending order,
/// its node and stamp as 8-byte big-endian integers. Decoding is a trust boundary and accepts canonical bytes only.
/// </summary>
internal static class ChangeCursor
{
    /// <summary>Origins one cursor holds: every node and epoch that wrote a database stays below this for years.</summary>
    public const int MaxOrigins = 256;

    private const byte Format = 1;

    public static string Encode(IReadOnlyDictionary<ReplicaId, Hlc> cursor)
    {
        var bytes = new byte[1 + (16 * cursor.Count)];
        bytes[0] = Format;
        var at = 1;
        foreach (var (origin, stamp) in cursor.OrderBy(pair => pair.Key.Value))
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at), origin.Value);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at + 8), stamp.Value);
            at += 16;
        }

        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>The cursor <paramref name="text"/> encodes, or null when it is not a canonical cursor.</summary>
    public static Dictionary<ReplicaId, Hlc>? Decode(string text)
    {
        if (text.Length > Base64Url.GetEncodedLength(1 + (16 * MaxOrigins)) || !Base64Url.IsValid(text, out var length) || length % 16 != 1)
        {
            return null;
        }

        var bytes = Base64Url.DecodeFromChars(text);
        if (bytes[0] != Format || Base64Url.EncodeToString(bytes) != text)
        {
            return null;
        }

        var cursor = new Dictionary<ReplicaId, Hlc>();
        ulong previous = 0;
        for (var at = 1; at < bytes.Length; at += 16)
        {
            var origin = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at));
            var stamp = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8));
            // Origin 0 is a repair, which the feed files under the original node; a stamp fits a signed 64-bit integer.
            if (origin <= previous || stamp is 0 or > long.MaxValue)
            {
                return null;
            }

            previous = origin;
            cursor[new ReplicaId(origin)] = new Hlc(stamp);
        }

        return cursor;
    }
}
