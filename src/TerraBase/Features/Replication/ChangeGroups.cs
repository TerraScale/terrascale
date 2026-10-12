using Google.Protobuf;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// One committed local transaction of a client database (docs/terrabase/multi-region-replication.md section 7): its
/// change id, which grows with commit order, and one delta per document it changed.
/// </summary>
/// <param name="ChangeId">Larger than every earlier group's of the database on this machine.</param>
/// <param name="Deltas">1 to <see cref="ChangeGroups.MaxDeltas"/> deltas.</param>
/// <param name="Stale">
/// Committed 24 h or more before it was read: each delta is then the document's whole state as of that commit. Peers may
/// have collected a newer deletion of it meanwhile (section 11), so they join it only where it cannot bring one back.
/// </param>
public sealed record OriginGroup(long ChangeId, IReadOnlyList<ChangeDelta> Deltas, bool Stale = false);

/// <summary>One document operation of a group: the entries it wrote at (<paramref name="Stamp"/>, the origin).</summary>
/// <param name="Collection">The document's collection: a collection name or a reserved metadata collection.</param>
/// <param name="Id">The document's id.</param>
/// <param name="Stamp">The operation's stamp.</param>
/// <param name="State"><see cref="DocumentState.DeltaAt"/> of the operation, plain values included.</param>
public sealed record ChangeDelta(string Collection, string Id, Hlc Stamp, DocumentState State);

/// <summary>
/// Where the peer streams read this machine's own change groups: the CDC tailer (slice R3). Only groups of local
/// writes: a group joined from a peer (its origin is another node, or 0 for a repair) is never returned, so nothing echoes.
/// </summary>
public interface IChangeGroupSource
{
    /// <summary>Every database that may hold groups to ship: <see cref="ChangeGroups.System"/> and each client database.</summary>
    IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The database's groups with a change id above <paramref name="after"/>, in change id order: at most
    /// <paramref name="maxGroups"/>, and no more than <paramref name="maxBytes"/> of encoded deltas unless the first group alone
    /// is larger. Empty when there are none.
    /// </summary>
    ValueTask<IReadOnlyList<OriginGroup>> ReadAsync(TenantContext database, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken);

    /// <summary>The last change id <paramref name="peer"/> acknowledged for the database (its <c>_tb_peers.acked</c>), or 0.</summary>
    ValueTask<long> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="peer"/> durably applied every group up to <paramref name="changeId"/>: the cursor a
    /// reconnecting stream resumes from, and what CDC retention may drop once every peer acknowledged it; and the peer's
    /// reported stable stamp, which bounds garbage collection here.
    /// </summary>
    ValueTask AckAsync(TenantContext database, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken);

    /// <summary>Completes once a group may have committed after the call (or when the source cannot tell): a wake-up, not a guarantee.</summary>
    Task ChangedAsync(CancellationToken cancellationToken);

    /// <summary>Records <paramref name="peer"/>'s stable stamp of the database, reported without an acknowledgement.</summary>
    ValueTask RecordStableAsync(TenantContext database, ReplicaId peer, Hlc stable, CancellationToken cancellationToken);

    /// <summary>Whether every group after <paramref name="after"/> is still held; false once retention cut some, so the peer refills.</summary>
    ValueTask<bool> RetainsAsync(TenantContext database, long after, CancellationToken cancellationToken);

    /// <summary>The database's newest change id, or 0: where a refilled peer's stream goes on.</summary>
    ValueTask<long> HeadAsync(TenantContext database, CancellationToken cancellationToken);
}

/// <summary>Where a receiver joins the groups an origin streams to it.</summary>
public interface IChangeGroupSink
{
    /// <summary>
    /// Joins <paramref name="groups"/> of <paramref name="origin"/> into the database, in order, and commits them durably before
    /// returning the highest stamp joined. Idempotent and commutative: a duplicate, replayed or reordered group changes
    /// nothing a join already holds. Null, with nothing applied, when this machine does not hold the database.
    /// </summary>
    ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, CancellationToken cancellationToken);

    /// <summary>
    /// Joins whole states anti-entropy read from a peer, as repairs (origin 0: never shipped again, and no origin's applied
    /// stamp moves), durably. Returns how many documents changed, or null when this machine does not hold the database.
    /// </summary>
    ValueTask<int?> RepairAsync(TenantContext database, IReadOnlyList<RepairState> states, CancellationToken cancellationToken);

    /// <summary>
    /// Records that this machine holds every event of <paramref name="origin"/> stamped below <paramref name="frontier"/>
    /// (its applied stamp rises there, section 11), when it holds the database.
    /// </summary>
    ValueTask RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, CancellationToken cancellationToken);
}

/// <summary>The wire form of change groups and its bounds; decoding is a trust boundary.</summary>
internal static class ChangeGroups
{
    /// <summary>Groups per batch.</summary>
    public const int MaxGroups = 1024;

    /// <summary>Deltas per group, as many as one transaction writes.</summary>
    public const int MaxDeltas = IStorageTransaction.MaxChangeGroupChanges;

    /// <summary>
    /// The system database (the catalog), placed in every region (docs/terrabase/multi-region-replication.md section 15).
    /// Its ids are no ULIDs, so no client database's identity ever equals it.
    /// </summary>
    public static TenantContext System { get; } = new("_system", PlanTier.Hobby, "_system", "_system");

    /// <summary>Deltas one group of <paramref name="database"/> may hold.</summary>
    public static int MaxDeltasOf(TenantContext database) => database == System ? IStorageTransaction.MaxSystemGroupChanges : MaxDeltas;

    /// <summary>Encoded groups per batch, before the LZ4 envelope; a single larger group still travels alone.</summary>
    public const int MaxBatchBytes = 2 * 1024 * 1024;

    public static V1.Database Wire(TenantContext database) =>
        new() { OrganizationId = database.OrganizationId, ProjectId = database.ProjectId, DatabaseId = database.DatabaseId };

    /// <exception cref="InvalidDataException">The ids are neither canonical ULIDs nor the system database's.</exception>
    public static TenantContext Tenant(V1.Database? database) =>
        database is null ? throw new InvalidDataException("a batch names its database")
        : database.OrganizationId == System.OrganizationId && database.ProjectId == System.ProjectId && database.DatabaseId == System.DatabaseId ? System
        : EntityIds.IsValid(database.OrganizationId) && EntityIds.IsValid(database.ProjectId) && EntityIds.IsValid(database.DatabaseId)
            ? new TenantContext(database.OrganizationId, PlanTier.Hobby, database.ProjectId, database.DatabaseId)
            : throw new InvalidDataException("a batch names a database by its organization, project and database ULIDs");

    /// <summary>The LZ4 envelope of <paramref name="groups"/>.</summary>
    public static ByteString Encode(IReadOnlyList<OriginGroup> groups)
    {
        var list = new V1.GroupList();
        foreach (var group in groups)
        {
            var wire = new V1.Group { ChangeId = checked((ulong)group.ChangeId), Stale = group.Stale };
            wire.Deltas.AddRange(group.Deltas.Select(delta => new V1.Delta
            {
                Collection = delta.Collection,
                Id = delta.Id,
                Stamp = delta.Stamp.Value,
                State = UnsafeByteOperations.UnsafeWrap(DocumentStateEncoding.Encode(delta.State)),
            }));
            list.Groups.Add(wire);
        }

        return UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(list.ToByteArray()));
    }

    /// <summary>
    /// The groups of a batch of <paramref name="database"/>, checked: 1 to <see cref="MaxGroups"/> groups with growing change
    /// ids that end at <paramref name="last"/>, each with 1 to <see cref="MaxDeltasOf"/> canonical deltas of valid
    /// collections and ids.
    /// </summary>
    /// <exception cref="InvalidDataException">Anything else.</exception>
    public static List<OriginGroup> Decode(TenantContext database, ByteString envelope, ulong last)
    {
        V1.GroupList list;
        try
        {
            list = V1.GroupList.Parser.ParseFrom(ApplicationEnvelope.Decode(envelope.Span, ReplicationProtocol.MaxApplicationBytes));
        }
        catch (InvalidProtocolBufferException error)
        {
            throw new InvalidDataException("a batch's groups are not a group list", error);
        }

        if (list.Groups.Count is 0 or > MaxGroups || list.Groups[^1].ChangeId != last)
        {
            throw new InvalidDataException("a batch holds 1 to 1024 groups, the last one at the batch's change id");
        }

        var groups = new List<OriginGroup>(list.Groups.Count);
        ulong previous = 0;
        foreach (var group in list.Groups)
        {
            if (group.ChangeId <= previous || group.ChangeId > long.MaxValue || group.Deltas.Count == 0 || group.Deltas.Count > MaxDeltasOf(database))
            {
                throw new InvalidDataException("groups have growing change ids and 1 to 1024 deltas (16,384 in the system database)");
            }

            previous = group.ChangeId;
            groups.Add(new OriginGroup((long)group.ChangeId, [.. group.Deltas.Select(delta => Delta(delta, database))], group.Stale));
        }

        return groups;
    }

    /// <summary>
    /// Whether a peer may name a document of <paramref name="database"/> by <paramref name="collection"/> and
    /// <paramref name="id"/>. A client collection is a table name and its ids are API ids; the system database's are whatever
    /// the catalog writes (paths, names, hashes), so only their size is checked: a key a receiver refused would hold its
    /// stream back for good.
    /// </summary>
    public static bool ValidKey(TenantContext database, string collection, string id) =>
        database == System
            ? collection.Length is > 0 and <= ushort.MaxValue && id.Length is > 0 and <= ushort.MaxValue
            : (DatabaseName.TryParse(collection, out _) || collection is DocumentDelta.Collections or DocumentDelta.Indexes or DocumentDelta.Unique)
                && id.Length is > 0 and <= 1024 && !id.Any(char.IsControl);

    private static ChangeDelta Delta(V1.Delta delta, TenantContext database)
    {
        if (!ValidKey(database, delta.Collection, delta.Id) || delta.Stamp == 0)
        {
            throw new InvalidDataException("a delta names a valid collection, an id of 1 to 1024 characters and a stamp");
        }

        return new ChangeDelta(delta.Collection, delta.Id, new Hlc(delta.Stamp), DocumentStateEncoding.Decode(delta.State.Span));
    }
}
