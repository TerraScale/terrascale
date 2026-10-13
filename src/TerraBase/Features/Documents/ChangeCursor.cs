using System.Buffers.Binary;
using System.Buffers.Text;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;

namespace TerraBase.Features.Documents;

/// <summary>
/// The opaque changes feed cursor: marker, issue stamp and issuing node, then per origin in ascending order its node,
/// stamp, retention floor and 32-byte prefix hash. Integers are 8-byte big-endian. Cross-machine resumes verify that
/// no event below a named stamp was omitted by the issuing machine. Decoding accepts canonical bytes only.
/// </summary>
internal static class ChangeCursor
{
    /// <summary>
    /// Origins one cursor holds: every node and epoch that wrote a database in the last weeks (an origin whose events all aged
    /// out leaves the cursors, <see cref="FeedCursor.Forget"/>).
    /// </summary>
    public const int MaxOrigins = 256;

    private const byte Marker = 1;
    private const int HeaderBytes = 17;
    private const int OriginBytes = 56;

    /// <exception cref="InvalidOperationException">More than <see cref="MaxOrigins"/> origins: no cursor this API would accept.</exception>
    public static string Encode(FeedCursor cursor)
    {
        if (cursor.Stamps.Count > MaxOrigins)
        {
            throw new InvalidOperationException($"a changes feed cursor holds at most {MaxOrigins} origins");
        }

        var bytes = new byte[HeaderBytes + (OriginBytes * cursor.Stamps.Count)];
        bytes[0] = Marker;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1), cursor.Issued.Value);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(9), cursor.Issuer.Value);
        var at = HeaderBytes;
        foreach (var (origin, stamp) in cursor.Stamps.OrderBy(pair => pair.Key.Value))
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at), origin.Value);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at + 8), stamp.Value);
            var prefix = cursor.Prefixes.GetValueOrDefault(origin) ?? FeedPrefix.Empty(default);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at + 16), prefix.Floor.Value);
            prefix.Hash.Span.CopyTo(bytes.AsSpan(at + 24, 32));
            at += OriginBytes;
        }

        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>The cursor <paramref name="text"/> encodes, or null when it is not a canonical cursor.</summary>
    public static FeedCursor? Decode(string text)
    {
        if (text.Length > Base64Url.GetEncodedLength(HeaderBytes + (OriginBytes * MaxOrigins)) || !Base64Url.IsValid(text, out var length) || length < HeaderBytes || (length - HeaderBytes) % OriginBytes != 0)
        {
            return null;
        }

        var bytes = Base64Url.DecodeFromChars(text);
        var issued = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(1));
        if (bytes[0] != Marker || issued is 0 or > long.MaxValue || Base64Url.EncodeToString(bytes) != text)
        {
            return null;
        }

        var stamps = new Dictionary<ReplicaId, Hlc>();
        var prefixes = new Dictionary<ReplicaId, FeedPrefix>();
        ulong previous = 0;
        for (var at = HeaderBytes; at < bytes.Length; at += OriginBytes)
        {
            var origin = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at));
            var stamp = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8));
            var floor = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 16));
            // Origin 0 is a repair, which the feed files under the repairing node; a stamp fits a signed 64-bit integer.
            if (origin <= previous || stamp is 0 or > long.MaxValue || floor > stamp)
            {
                return null;
            }

            previous = origin;
            stamps[new ReplicaId(origin)] = new Hlc(stamp);
            prefixes[new ReplicaId(origin)] = new FeedPrefix(new Hlc(floor), bytes.AsMemory(at + 24, 32));
        }

        return new FeedCursor(stamps, new Hlc(issued))
        {
            Issuer = new ReplicaId(BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(9))),
            Prefixes = prefixes,
        };
    }
}
