using System.Buffers;
using System.Diagnostics;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;

namespace TerraScale.QuicRpc.Messages;

public sealed record ChangefeedSubscribeMessage(Ulid DatabaseId, string After) : IBinaryMessage<ChangefeedSubscribeMessage>
{
    public const string InvalidBookmark = "invalid_bookmark";

    public static ChangefeedSubscribeMessage From(ChangefeedSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new ChangefeedSubscribeMessage(subscription.DatabaseId, subscription.After.Encode());
    }

    public static ChangefeedSubscribeMessage ReadFrom(ref WireReader reader) => new(reader.ReadUlid(), reader.ReadString());

    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUlid(DatabaseId);
        writer.WriteString(After);
    }

    public ChangefeedSubscriptionDecodeOutcome ToSubscription() =>
        CausalBookmark.TryDecode(After, out var after)
            ? new ChangefeedSubscription(DatabaseId, after)
            : new QuicRemoteError(InvalidBookmark, "The changefeed subscription carries a malformed causal bookmark.");
}

public sealed record ChangefeedEntryMessage(
    Ulid DatabaseId,
    Ulid TxId,
    string OriginRegion,
    long Sequence,
    ulong SchemaEpoch,
    ConsistencyMode Mode,
    ReadOnlyMemory<byte> DeltaPayload,
    long CommittedAtUnixMs) : IBinaryMessage<ChangefeedEntryMessage>
{
    public const string InvalidEntry = "invalid_changefeed_entry";

    private static readonly long MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static ChangefeedEntryMessage From(ChangefeedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ChangefeedEntryMessage(
            entry.DatabaseId,
            entry.TxId,
            entry.OriginRegion,
            entry.Sequence,
            entry.SchemaEpoch,
            entry.Mode,
            entry.DeltaPayload,
            entry.CommittedAtUtc.ToUnixTimeMilliseconds());
    }

    public static ChangefeedEntryMessage ReadFrom(ref WireReader reader) => new(
        reader.ReadUlid(),
        reader.ReadUlid(),
        reader.ReadString(),
        reader.ReadInt64(),
        reader.ReadUInt64(),
        reader.ReadBool() ? ConsistencyMode.Strong : ConsistencyMode.Fast,
        reader.ReadBytes(),
        reader.ReadInt64());

    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUlid(DatabaseId);
        writer.WriteUlid(TxId);
        writer.WriteString(OriginRegion);
        writer.WriteInt64(Sequence);
        writer.WriteUInt64(SchemaEpoch);
        writer.WriteBool(Mode switch
        {
            ConsistencyMode.Fast => false,
            ConsistencyMode.Strong => true,
            _ => throw new UnreachableException(),
        });
        writer.WriteBytes(DeltaPayload.Span);
        writer.WriteInt64(CommittedAtUnixMs);
    }

    public ChangefeedEntryDecodeOutcome ToEntry() =>
        Sequence <= 0 || OriginRegion.Length == 0 || CommittedAtUnixMs < MinUnixMs || CommittedAtUnixMs > MaxUnixMs
            ? new QuicRemoteError(InvalidEntry, "The changefeed entry carries an invalid sequence, origin or commit time.")
            : new ChangefeedEntry(
                DatabaseId,
                TxId,
                OriginRegion,
                Sequence,
                SchemaEpoch,
                Mode,
                DeltaPayload,
                DateTimeOffset.FromUnixTimeMilliseconds(CommittedAtUnixMs));
}
