using System.Buffers.Binary;
using System.Buffers.Text;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;

namespace TerraBase.Features.Documents;

/// <summary>
/// The changes feed cursor (docs/terrabase/multi-region-replication.md section 14): when it was issued, and per origin node
/// the highest stamp delivered, valid on any machine. Opaque to clients: base64url of a format byte (1), the issue stamp,
/// then per origin in ascending order its node and stamp, all as 8-byte big-endian integers. Decoding is a trust boundary
/// and accepts canonical bytes only.
/// </summary>
internal static class ChangeCursor
{
    /// <summary>
    /// Origins one cursor holds: every node and epoch that wrote a database in the last weeks (an origin whose events all aged
    /// out leaves the cursors, <see cref="FeedCursor.Forget"/>).
    /// </summary>
    public const int MaxOrigins = 256;

    private const byte Format = 1;

    /// <exception cref="InvalidOperationException">More than <see cref="MaxOrigins"/> origins: no cursor this API would accept.</exception>
    public static string Encode(FeedCursor cursor)
    {
        if (cursor.Stamps.Count > MaxOrigins)
        {
            throw new InvalidOperationException($"a changes feed cursor holds at most {MaxOrigins} origins");
        }

        var bytes = new byte[9 + (16 * cursor.Stamps.Count)];
        bytes[0] = Format;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1), cursor.Issued.Value);
        var at = 9;
        foreach (var (origin, stamp) in cursor.Stamps.OrderBy(pair => pair.Key.Value))
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at), origin.Value);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at + 8), stamp.Value);
            at += 16;
        }

        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>The cursor <paramref name="text"/> encodes, or null when it is not a canonical cursor.</summary>
    public static FeedCursor? Decode(string text)
    {
        if (text.Length > Base64Url.GetEncodedLength(9 + (16 * MaxOrigins)) || !Base64Url.IsValid(text, out var length) || length % 16 != 9)
        {
            return null;
        }

        var bytes = Base64Url.DecodeFromChars(text);
        var issued = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(1));
        if (bytes[0] != Format || issued is 0 or > long.MaxValue || Base64Url.EncodeToString(bytes) != text)
        {
            return null;
        }

        var stamps = new Dictionary<ReplicaId, Hlc>();
        ulong previous = 0;
        for (var at = 9; at < bytes.Length; at += 16)
        {
            var origin = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at));
            var stamp = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8));
            // Origin 0 is a repair, which the feed files under the original node; a stamp fits a signed 64-bit integer.
            if (origin <= previous || stamp is 0 or > long.MaxValue)
            {
                return null;
            }

            previous = origin;
            stamps[new ReplicaId(origin)] = new Hlc(stamp);
        }

        return new FeedCursor(stamps, new Hlc(issued));
    }
}
