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
public sealed record OriginGroup(long ChangeId, IReadOnlyList<ChangeDelta> Deltas);

/// <summary>A document operation or immutable catalog-version dependency; the stamp advances the sending origin only for its own group.</summary>
/// <param name="Collection">The document's collection: a collection name or a reserved metadata collection.</param>
/// <param name="Id">The document's id.</param>
/// <param name="Stamp">The operation's stamp.</param>
/// <param name="State"><see cref="DocumentState.DeltaAt"/> of the operation, or the unchanged catalog-version state carried with dependent rows.</param>
public sealed record ChangeDelta(string Collection, string Id, Hlc Stamp, DocumentState State);

/// <summary>
/// A peer's cursor in one incarnation of this machine's copy of a database: change ids and acks name rows of that file's
/// change log only, and every copy, restore or rebuild is a new incarnation whose ids restart.
/// </summary>
/// <param name="Incarnation">The copy the cursor belongs to.</param>
/// <param name="Acked">The last change id the peer acknowledged in it, or 0.</param>
public readonly record struct SourceCursor(Ulid Incarnation, long Acked)
{
    /// <summary>A copied or restored peer row must refill this incarnation before resuming its change log.</summary>
    public bool RequiresRefill { get; init; }

    /// <summary>The peer's reported stable lower bound, belonging to this source incarnation only.</summary>
    public Hlc Stable { get; init; }
}

/// <summary>Groups read from one incarnation of this machine's copy of a database.</summary>
/// <param name="Incarnation">The incarnation the read's own storage transaction found in place.</param>
/// <param name="Groups">The groups, in change id order; empty when there are none.</param>
/// <param name="Retained">
/// Whether every group after the read's start was still held in the same read (<see cref="IChangeGroupSource.RetainsAsync"/>):
/// false once retention cut some, so the peer refills.
/// </param>
public sealed record SourceGroups(Ulid Incarnation, IReadOnlyList<OriginGroup> Groups, bool Retained);

/// <summary>
/// Where the peer streams read this machine's own change groups: the CDC tailer (slice R3). Only groups of local
/// writes: a group joined from a peer (its origin is another node, or 0 for a repair) is never returned. System groups
/// include the supported catalog-version state as an immutable dependency, without rewriting it or raising the sending origin's frontier.
/// Every read and write but <see cref="AckedAsync"/> names the incarnation it expects, checked inside its storage
/// transaction (for writes, under the writer), and is refused (null or false, nothing written) when the copy in place is
/// another or this machine no longer holds the database.
/// </summary>
public interface IChangeGroupSource
{
    /// <summary>Every database that may hold groups to ship: <see cref="ChangeGroups.System"/> and each client database.</summary>
    IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The database's groups with a change id above <paramref name="after"/> in <paramref name="incarnation"/>, in change id
    /// order: at most <paramref name="maxGroups"/>, and no more than <paramref name="maxBytes"/> of encoded groups, keys and framing unless the
    /// first group alone is larger, with the incarnation the read's transaction found and whether it still held every group
    /// after <paramref name="after"/>. Null when refused.
    /// </summary>
    ValueTask<SourceGroups?> ReadAsync(
        TenantContext database, Ulid incarnation, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// The copy in place and the last change id <paramref name="peer"/> acknowledged in it (its <c>_tb_peers.acked</c>), or 0
    /// when it acknowledged nothing of this incarnation. A copied peer row sets <see cref="SourceCursor.RequiresRefill"/>
    /// until a refill ACK persists the new scope. Null when this machine does not hold the database, or when its copy was
    /// replaced during this read (ask again).
    /// </summary>
    ValueTask<SourceCursor?> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="peer"/> durably applied every group of <paramref name="incarnation"/> up to
    /// <paramref name="changeId"/>: the cursor a reconnecting stream resumes from, and what CDC retention may drop once every
    /// peer acknowledged it; and the peer's reported stable stamp, which bounds garbage collection here. False, with nothing
    /// recorded, when refused.
    /// </summary>
    ValueTask<bool> AckAsync(TenantContext database, Ulid incarnation, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken);

    /// <summary>
    /// Advances the shape reads' remote durable frontier (FR-25) with an eligible live acknowledgement: a machine of another
    /// placed region durably holds every own group of <paramref name="incarnation"/> after <paramref name="from"/> up to
    /// <paramref name="through"/>, each received in a live batch. Joined commits are held when their origin is one of
    /// <paramref name="held"/> (<see cref="IRemoteDurability.HeldOriginsAsync"/>). False when the copy in place is another.
    /// </summary>
    ValueTask<bool> ProveAsync(TenantContext database, Ulid incarnation, long from, long through, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken);

    /// <summary>Completes once a group may have committed after the call (or when the source cannot tell): a wake-up, not a guarantee.</summary>
    Task ChangedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to database invalidations after committed local groups, catalog changes or copy publication. Subscribe
    /// before initial discovery; callbacks may repeat or race disposal, and must only enqueue work without throwing.
    /// </summary>
    IDisposable Subscribe(Action<TenantContext> changed);

    /// <summary>
    /// Records <paramref name="peer"/>'s stable stamp of the database, reported without an acknowledgement to a frontier of
    /// <paramref name="incarnation"/>. False, with nothing recorded, when refused.
    /// </summary>
    ValueTask<bool> RecordStableAsync(TenantContext database, Ulid incarnation, ReplicaId peer, Hlc stable, CancellationToken cancellationToken);

    /// <summary>
    /// Whether every group of <paramref name="incarnation"/> after <paramref name="after"/> is still held; false once retention
    /// cut some, so the peer refills; null when refused.
    /// </summary>
    ValueTask<bool?> RetainsAsync(TenantContext database, Ulid incarnation, long after, CancellationToken cancellationToken);

    /// <summary>The newest change id of <paramref name="incarnation"/>, or 0: where a refilled peer's stream goes on; null when refused.</summary>
    ValueTask<long?> HeadAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken);

    /// <summary>
    /// A complete retained transaction boundary no greater than <paramref name="through"/>, clamped to the oldest
    /// retained boundary. Null through selects the oldest boundary up to 64 MiB of capture, otherwise the previously
    /// captured head; null result means the copy was refused.
    /// </summary>
    ValueTask<long?> RetainedAfterAsync(TenantContext database, Ulid incarnation, long? through, long head, CancellationToken cancellationToken);
}

/// <summary>Where a receiver joins the groups an origin streams to it.</summary>
public interface IChangeGroupSink
{
    /// <summary>
    /// Joins <paramref name="groups"/> of <paramref name="origin"/> into the database, in order, and commits them durably before
    /// returning the highest stamp joined. Idempotent and commutative: a duplicate, replayed or reordered group changes
    /// nothing a join already holds. Each writer refuses coverage below <paramref name="expected"/> before joining.
    /// Null, with nothing applied, when this machine does not hold the database.
    /// </summary>
    ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null);

    /// <summary>
    /// Joins whole states anti-entropy read from a peer, as repairs (origin 0: never shipped again, and no origin's applied
    /// stamp moves), durably. Every chunk checks <paramref name="incarnation"/> under its writer. Returns how many documents
    /// changed, or null when this machine no longer holds that incarnation; earlier chunks may already have committed.
    /// </summary>
    ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken);

    /// <summary>
    /// Records that this machine holds every event of <paramref name="origin"/> stamped below <paramref name="frontier"/>
    /// (its applied stamp rises there, section 11), durably, after checking <paramref name="expected"/> under its writer.
    /// Returns the persisted incarnation, or null when the database is not held or the named receiver copy was replaced.
    /// </summary>
    ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken);
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

    // One group's complete GroupList contribution, including keys and protobuf framing, before compression.
    internal static int EncodedSize(long changeId, IReadOnlyList<ChangeDelta> deltas)
    {
        var size = 1 + CodedOutputStream.ComputeUInt64Size(checked((ulong)changeId));
        foreach (var delta in deltas)
        {
            var stateBytes = DocumentStateEncoding.Encode(delta.State).Length;
            var deltaBytes = 1 + CodedOutputStream.ComputeStringSize(delta.Collection)
                + 1 + CodedOutputStream.ComputeStringSize(delta.Id)
                + 9 + 1 + CodedOutputStream.ComputeLengthSize(stateBytes) + stateBytes;
            size = checked(size + 1 + CodedOutputStream.ComputeLengthSize(deltaBytes) + deltaBytes);
        }

        return checked(1 + CodedOutputStream.ComputeLengthSize(size) + size);
    }

    /// <summary>The wire name of <paramref name="database"/>, of <paramref name="incarnation"/> when there is one.</summary>
    public static V1.Database Wire(TenantContext database, Ulid? incarnation) =>
        new()
        {
            OrganizationId = database.OrganizationId,
            ProjectId = database.ProjectId,
            DatabaseId = database.DatabaseId,
            Incarnation = incarnation is { } named ? ByteString.CopyFrom(named.ToByteArray()) : ByteString.Empty,
        };

    /// <summary>The incarnation a wire database names, or null when it names none.</summary>
    /// <exception cref="InvalidDataException">It is not 16 bytes, or is the empty ULID.</exception>
    public static Ulid? Incarnation(V1.Database? database) =>
        database is null || database.Incarnation.IsEmpty ? null
        : database.Incarnation.Length == 16 && new Ulid(database.Incarnation.Span) is var incarnation && incarnation != Ulid.Empty ? incarnation
        : throw new InvalidDataException("a database's incarnation is a 16-byte ULID");

    /// <summary>The incarnation a wire database must name.</summary>
    /// <exception cref="InvalidDataException">It names none, or a malformed one.</exception>
    public static Ulid RequiredIncarnation(V1.Database? database) =>
        Incarnation(database) ?? throw new InvalidDataException("the frame names the incarnation of its database");

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
            var wire = new V1.Group { ChangeId = checked((ulong)group.ChangeId) };
            wire.Deltas.AddRange(group.Deltas.Select(delta => new V1.Delta
            {
                Collection = delta.Collection,
                Id = delta.Id,
                Stamp = delta.Stamp.Value,
                State = UnsafeByteOperations.UnsafeWrap(DocumentStateEncoding.Encode(delta.State)),
            }));
            list.Groups.Add(wire);
        }

        return UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(list));
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
            groups.Add(new OriginGroup((long)group.ChangeId, [.. group.Deltas.Select(delta => Delta(delta, database))]));
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
