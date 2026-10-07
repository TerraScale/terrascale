namespace TerraBase.Engine.Lattices;

/// <summary>
/// The writer of a <see cref="Dot"/>: one member of one region, in one epoch of its life, packed as region (16 bits),
/// member (32 bits) and epoch (16 bits). The epoch rises on every unclean start, so a member that lost writes it may
/// already have shipped (an OS crash under <c>synchronous = NORMAL</c>, a restored disk) never mints a dot it minted
/// before: reusing one would silently drop a write on every replica that holds the first (the Riak kv679 class of
/// bug). A member past epoch 65535 registers again under a new member number.
/// </summary>
/// <param name="Value">The packed id.</param>
// anna-full (TS-648): per-core actors give each actor its own replica id; the epoch bits then also carry the actor index.
public readonly record struct ReplicaId(ulong Value)
{
    /// <summary>The region (TS-643: replication across regions keeps dots of every region distinct).</summary>
    public ushort Region => (ushort)(Value >> 48);

    /// <summary>The member, numbered once per registration.</summary>
    public uint Member => (uint)(Value >> 16);

    /// <summary>How many unclean starts the member had before minting with this id.</summary>
    public ushort Epoch => (ushort)Value;

    /// <summary>The id of <paramref name="member"/> of <paramref name="region"/> in <paramref name="epoch"/>.</summary>
    public static ReplicaId Of(ushort region, uint member, ushort epoch) => new(((ulong)region << 48) | ((ulong)member << 16) | epoch);
}
