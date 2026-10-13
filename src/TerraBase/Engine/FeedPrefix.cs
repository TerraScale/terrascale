using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

/// <summary>A retained origin prefix: its retention floor and a hash of every scanned event above it.</summary>
public sealed record FeedPrefix(Hlc Floor, ReadOnlyMemory<byte> Hash)
{
    /// <summary>The empty prefix above a retention floor.</summary>
    public static FeedPrefix Empty(Hlc floor) => new(floor, new byte[32]);

    /// <summary>Appends an event's identity, including events excluded by a collection filter.</summary>
    public FeedPrefix Append(Hlc stamp, string collection, string id, bool deleted)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Hash.Span);
        Span<byte> value = stackalloc byte[9];
        BinaryPrimitives.WriteUInt64BigEndian(value, stamp.Value);
        value[8] = deleted ? (byte)1 : (byte)0;
        hash.AppendData(value);
        foreach (var text in new[] { collection, id })
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            BinaryPrimitives.WriteInt32BigEndian(value, bytes.Length);
            hash.AppendData(value[..4]);
            hash.AppendData(bytes);
        }

        return new FeedPrefix(Floor, hash.GetHashAndReset());
    }
}
