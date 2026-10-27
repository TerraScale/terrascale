using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

/// <summary>What a client database's local writes mint with: this member's replica id and its hybrid clock.</summary>
/// <param name="Id">The replica id of this process (the data directory's replica identity file).</param>
/// <param name="Clock">The member's clock.</param>
internal sealed record LocalReplica(ReplicaId Id, MemberClock Clock)
{
    /// <summary>When this process began (Unix ms on the member's clock): a peer counts as gone for 24 h only once this one has been up that long.</summary>
    public long StartedMilliseconds { get; } = Clock.Tick().Milliseconds;

    /// <summary>
    /// The Unix ms before which a peer last heard from counts as gone (24 h, section 11), or <see cref="long.MinValue"/> while
    /// this process has been up less than that and has not yet heard every listed machine: a machine back after a long absence
    /// heard from nobody, and that says nothing about its peers. Once it hears every listed machine it is not absent, so a
    /// restart (every deploy) does not restart the 24 h.
    /// </summary>
    public long GoneBefore(long nowMilliseconds) =>
        nowMilliseconds - StartedMilliseconds >= Day || Presence.Current() is not null ? nowMilliseconds - Day : long.MinValue;

    public PeerPresence Presence { get; } = new();

    public bool Gone(ReplicaId node, long seenMilliseconds, long nowMilliseconds) =>
        seenMilliseconds < GoneBefore(nowMilliseconds) && Presence.Absent(node, nowMilliseconds - Day);

    private const long Day = 86_400_000;
}
