using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>
/// The opaque changes feed cursor: marker, issue stamp, issuing node and machine, then per origin in ascending order its node,
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
    private const int HeaderBytes = 18;
    private const int MaxMachineBytes = 63;
    private const int OriginBytes = 56;

    /// <summary>Maximum decoded cursor length, including the issuing machine and every origin.</summary>
    public const int MaxBytes = 17 + HeaderBytes + MaxMachineBytes + (OriginBytes * MaxOrigins);

    /// <exception cref="InvalidOperationException">More than <see cref="MaxOrigins"/> origins: no cursor this API would accept.</exception>
    public static string Encode(FeedCursor cursor) => Encode(cursor, default);

    /// <summary>Encodes the HTTP feed cursor with its database and canonical collection-filter scope.</summary>
    public static string Encode(FeedCursor cursor, ReadOnlyMemory<byte> scope)
    {
        if (!scope.IsEmpty && scope.Length != 16)
        {
            throw new ArgumentException("a feed scope is 16 bytes", nameof(scope));
        }

        if (cursor.Stamps.Count > MaxOrigins)
        {
            throw new InvalidOperationException($"a changes feed cursor holds at most {MaxOrigins} origins");
        }

        var machine = cursor.Machine ?? string.Empty;
        if (machine.Length > 0 && !TerraBaseOptions.IsMachineId(machine))
        {
            throw new InvalidOperationException("a changes feed cursor names a valid issuing machine");
        }

        var envelope = scope.IsEmpty ? 0 : 17;
        var bytes = new byte[envelope + HeaderBytes + machine.Length + (OriginBytes * cursor.Stamps.Count)];
        if (envelope > 0)
        {
            bytes[0] = 2;
            scope.Span.CopyTo(bytes.AsSpan(1, 16));
        }

        var data = bytes.AsSpan(envelope);
        data[0] = Marker;
        BinaryPrimitives.WriteUInt64BigEndian(data[1..], cursor.Issued.Value);
        BinaryPrimitives.WriteUInt64BigEndian(data[9..], cursor.Issuer.Value);
        data[17] = (byte)machine.Length;
        _ = Encoding.ASCII.GetBytes(machine, data[HeaderBytes..]);
        var at = envelope + HeaderBytes + machine.Length;
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

    /// <summary>Database identity and collection set bound into HTTP feed cursors; this is not authentication.</summary>
    public static ReadOnlyMemory<byte> Scope(TenantContext database, IReadOnlySet<string>? collections) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{database.OrganizationId}\0{database.ProjectId}\0{database.DatabaseId}\0{(collections is null ? "*" : string.Join('\0', collections.Order(StringComparer.Ordinal)))}")).AsMemory(0, 16);

    /// <summary>The cursor <paramref name="text"/> encodes, or null when it is not a canonical cursor.</summary>
    public static FeedCursor? Decode(string text) => Decode(text, out _);

    /// <summary>Decodes a cursor and exposes the HTTP envelope scope, empty for internal shape cursors.</summary>
    public static FeedCursor? Decode(string text, out ReadOnlyMemory<byte> scope)
    {
        scope = default;
        if (text.Length > Base64Url.GetEncodedLength(MaxBytes) || !Base64Url.IsValid(text, out var length) || length < HeaderBytes)
        {
            return null;
        }

        var bytes = Base64Url.DecodeFromChars(text);
        if (Base64Url.EncodeToString(bytes) != text)
        {
            return null;
        }

        if (bytes[0] == 2)
        {
            if (bytes.Length < 17 + HeaderBytes)
            {
                return null;
            }

            scope = bytes.AsMemory(1, 16);
            bytes = bytes[17..];
        }

        var header = HeaderBytes + bytes[17];
        if (bytes[17] > MaxMachineBytes || header > bytes.Length || (bytes.Length - header) % OriginBytes != 0 || (bytes.Length - header) / OriginBytes > MaxOrigins)
        {
            return null;
        }

        var machine = Encoding.ASCII.GetString(bytes.AsSpan(HeaderBytes, bytes[17]));
        var issued = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(1));
        if (bytes[0] != Marker || issued is 0 or > long.MaxValue
            || (machine.Length > 0 && !TerraBaseOptions.IsMachineId(machine)))
        {
            return null;
        }

        var stamps = new Dictionary<ReplicaId, Hlc>();
        var prefixes = new Dictionary<ReplicaId, FeedPrefix>();
        ulong previous = 0;
        for (var at = header; at < bytes.Length; at += OriginBytes)
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
            Machine = machine,
            Prefixes = prefixes,
        };
    }
}
