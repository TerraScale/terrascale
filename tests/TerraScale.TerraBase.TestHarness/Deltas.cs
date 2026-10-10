using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;

namespace TerraBase.Testing;

/// <summary>
/// Replicated changes as another member would send them (docs/design/anna-core.md, Documents): register deltas written by
/// a replica id of their own, for tests that drive a node's receiving side directly.
/// </summary>
public static class Deltas
{
    /// <summary>A replica id no node draws: region 0xFFFF, a random member, epoch 0.</summary>
    public static ReplicaId NewReplica() => ReplicaId.Of(ushort.MaxValue, BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)) | 1, 0);

    /// <summary>
    /// The delta of <paramref name="replica"/> (a new one by default) writing <paramref name="json"/>, or a tombstone when
    /// it is <see langword="null"/>, at <paramref name="stamp"/> (now by default) over <paramref name="over"/>.
    /// </summary>
    public static DocumentDelta Write(
        string collection, string id, string? json, ReplicaId? replica = null, Hlc? stamp = null, MvRegister<DocumentWrite>? over = null)
    {
        var at = stamp ?? new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16);
        var write = json is null ? DocumentWrite.Tombstone(at) : DocumentWrite.Live(at, Encoding.UTF8.GetBytes(json));
        return new DocumentDelta(collection, id, (over ?? new MvRegister<DocumentWrite>()).WriteDelta(replica ?? NewReplica(), write));
    }

    /// <summary>A change group of <paramref name="changes"/> without dependencies.</summary>
    public static CapturedGroup Group(params DocumentDelta[] changes) => new([], changes);

    /// <summary>The bytes of collections, ids and encoded registers of <paramref name="changes"/>, which the group limits bound.</summary>
    public static long ContentBytes(IReadOnlyList<DocumentDelta> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return changes.Sum(change => (long)Encoding.UTF8.GetByteCount(change.Collection) + Encoding.UTF8.GetByteCount(change.Id)
            + LatticeEncoding.Encode(change.State).Length);
    }

    /// <summary>The JSON of the last-writer-wins write of <paramref name="change"/>, or <see langword="null"/> for a tombstone.</summary>
    public static string? Json(DocumentDelta change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var winner = change.State.Values.MaxBy(value => (value.Value.Stamp.Value, value.Key.Replica.Value, value.Key.Counter)).Value;
        return winner.IsTombstone ? null : Encoding.UTF8.GetString(winner.Body);
    }
}
