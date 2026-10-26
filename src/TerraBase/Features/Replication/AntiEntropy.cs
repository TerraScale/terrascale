using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Google.Protobuf;
using Grpc.Core;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>
/// Anti-entropy and refill (docs/terrabase/multi-region-replication.md section 10). Every 60 s, for each database and each
/// other machine whose region holds it, this machine compares the peer's 4096 bucket digests with its own and joins the
/// peer's states of every bucket that differed in two consecutive rounds (so in-flight streams settle first), as repairs.
/// It repairs what the streams cannot send again: a lost apply, a file restored to an older copy. A refill (a sender no
/// longer holds this machine's gap) joins every differing bucket at once. Both sides run rounds, so each pulls what it lacks.
/// </summary>
/// <remarks>
/// Without garbage collection (R7) a repair merges old state safely. Once tombstones are collected, a machine away past the
/// GC horizon must refill by copying instead of merging (section 10), or it would bring collected deletions back.
/// </remarks>
internal sealed class AntiEntropy : BackgroundService
{
    internal static readonly TimeSpan Round = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DigestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(10);
    private const int DigestBytes = 4096 * 16;
    private const int MaxPageStates = ChangeGroups.MaxDeltas;

    // A repair's bucket read names the requesting region, to which the source attributes the pages it sends.
    internal const string RegionHeader = "ts-region";

    private readonly IMembership _membership;
    private readonly PeerCredential _credential;
    private readonly IChangeGroupSource _source;
    private readonly IReplicaStates _states;
    private readonly IChangeGroupSink _sink;
    private readonly ITenantDirectory _tenants;
    private readonly TimeProvider _time;
    private readonly ILogger<AntiEntropy> _logger;
    private readonly Counter<long> _repairs;
    private readonly IUsageMeter _usage;

    // Per (database, peer machine): the buckets that differed in the last round, when it began and the copies it compared.
    private readonly ConcurrentDictionary<(TenantContext Database, string Peer), (HashSet<int> Buckets, long At, Ulid Local, Ulid Source)> _differed = new();

    // Per peer machine: the timestamp as of which its system catalog was last proved held in this machine's copy Local.
    private readonly ConcurrentDictionary<string, (long At, Ulid Local)> _catalogHeld = new(StringComparer.Ordinal);

    public AntiEntropy(
        IMembership membership, IChangeGroupSource source, IReplicaStates states, IChangeGroupSink sink, ITenantDirectory tenants, TimeProvider time,
        IMeterFactory meters, ILogger<AntiEntropy> logger, PeerCredential credential, IUsageMeter usage)
    {
        _credential = credential;
        _usage = usage;
        (_membership, _source, _states, _sink, _tenants, _time, _logger) = (membership, source, states, sink, tenants, time, logger);
        _repairs = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.replication.repairs", "{document}",
            "Documents anti-entropy changed, by the peer region they came from.");
    }

    // The system catalog runs its own rounds: a slow tenant repair (up to its 10 min read) must not age the catalog proofs
    // shape keys depend on (ICatalogFreshness).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RoundsAsync(system: true, stoppingToken), RoundsAsync(system: false, stoppingToken));

    private async Task RoundsAsync(bool system, CancellationToken stoppingToken)
    {
        // Tenant rounds read whole databases: bulk work. The catalog's rounds keep its proofs fresh and stay ordinary.
        using var bulk = system ? null : StorageWork.Bulk();
        using var timer = new PeriodicTimer(Round, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunRoundAsync(stoppingToken, system).ConfigureAwait(false);
            }
            catch (RpcException error) when (stoppingToken.IsCancellationRequested && error.StatusCode == StatusCode.Cancelled)
            {
                // A peer call the shutdown cancels throws RpcException, not OperationCanceledException: stop as canceled.
                stoppingToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                // A fault escaping this worker stops the host (exit 134): a round that fails (busy storage while listing) waits for the next.
                _logger.ZLogWarning(error, $"Anti-entropy round failed; the next round tries again");
            }
        }
    }

    /// <summary>
    /// One round over every database (only the system catalog, or only the others, when <paramref name="system"/> says so)
    /// and peer; a peer that fails is tried again next round.
    /// </summary>
    internal async Task RunRoundAsync(CancellationToken cancellationToken, bool? system = null)
    {
        var peers = Peers(await _membership.RefreshAsync(cancellationToken).ConfigureAwait(false));
        await foreach (var database in _source.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (system is { } only && only != (database == ChangeGroups.System))
            {
                continue;
            }

            // The system database is placed in every region.
            DatabasePlacement? placement;
            try
            {
                placement = database == ChangeGroups.System ? null
                    : await _tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false) is TenantDatabaseRecord record ? record.Placement
                    : null;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // Overload refuses the catalog read (StorageBusyException). Escaping would fault this worker, which stops the
                // host and aborts the process (exit 134); skip the database until the next round instead.
                _logger.ZLogWarning(error, $"Anti-entropy could not read the placement of database {database.DatabaseId:@Database}; the next round tries again");
                continue;
            }

            if (placement is null && database != ChangeGroups.System)
            {
                continue;
            }

            foreach (var peer in peers.Where(peer => placement?.Includes(peer.Region) ?? true))
            {
                try
                {
                    _ = await CompareAsync(database, peer, force: false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.ZLogWarning(error, $"Anti-entropy with machine {peer.Name:@Peer} in {peer.Region:@Region} failed; the next round tries again");
                }
            }
        }
    }

    /// <summary>
    /// Joins every bucket of <paramref name="database"/> that differs from <paramref name="incarnation"/> of the copy of the
    /// machine named <paramref name="machine"/>. Returns the documents changed, or null when the refill certifies nothing:
    /// that machine is not listed or no longer holds that incarnation, this one does not hold the database, or a page was
    /// refused.
    /// </summary>
    internal async Task<int?> RefillAsync(TenantContext database, string machine, Hlc horizon, Ulid incarnation, CancellationToken cancellationToken)
    {
        var peer = Peers(await _membership.RefreshAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(member => member.Name == machine);
        // A copy or restore refills before any batch has created the local file; catalog placement still controls creation.
        if (peer is not null && database != ChangeGroups.System
            && (await _tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is not true
                || await _tenants.EnsureLocalDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not Done))
        {
            return null;
        }

        return peer is null ? null : await CompareAsync(database, peer, force: true, cancellationToken, horizon, incarnation).ConfigureAwait(false);
    }

    internal Task<int?> PullAsync(TenantContext database, ClusterMember peer, CancellationToken cancellationToken) =>
        CompareAsync(database, peer, force: true, cancellationToken);

    /// <summary>
    /// The machines whose system catalog a comparison proved held here less than <paramref name="window"/> ago, counting
    /// only proofs into the copy still in place (a rebuilt or restored catalog starts without any).
    /// </summary>
    internal async ValueTask<HashSet<string>> CatalogHeldWithinAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var held = new Dictionary<string, long>(StringComparer.Ordinal);
        var inPlace = new Dictionary<Ulid, bool>();
        foreach (var (peer, proof) in _catalogHeld)
        {
            if (_time.GetElapsedTime(proof.At) >= window)
            {
                continue;
            }

            if (!inPlace.TryGetValue(proof.Local, out var current))
            {
                inPlace[proof.Local] = current = await LocalHeldAsync(ChangeGroups.System, proof.Local, cancellationToken).ConfigureAwait(false);
            }

            if (current)
            {
                held[peer] = proof.At;
            }
        }

        // A later incarnation read can outlive a proof already checked above.
        return held.Where(proof => _time.GetElapsedTime(proof.Value) < window).Select(proof => proof.Key).ToHashSet(StringComparer.Ordinal);
    }

    // Monotonic within one local copy: a slow comparison that began earlier never moves a peer's proof back.
    private void CatalogHeld(TenantContext database, ClusterMember peer, long? at, Ulid local)
    {
        if (database == ChangeGroups.System && at is { } began)
        {
            _ = _catalogHeld.AddOrUpdate(peer.Name, (began, local),
                (_, known) => known.Local == local ? (Math.Max(known.At, began), local) : (began, local));
        }
    }

    /// <summary>The other members of <paramref name="view"/> with a peer listener: the machines anti-entropy compares with.</summary>
    internal static List<ClusterMember> Peers(ClusterView view, string self) =>
        [.. view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere).Where(member => member.Name != self && member.PeerAddress.Length > 0)];

    private List<ClusterMember> Peers(ClusterView view) => Peers(view, _membership.Self.Name);

    // Compares digests with the peer and joins the buckets to repair; null when this machine does not hold the database.
    // With a horizon (a refill), it copies: a local state of a repaired bucket that the peer lacks and that lies wholly below
    // the peer's horizon was collected there (section 11), so it goes here too instead of coming back by a later round.
    // A refill names the peer's incarnation it was asked for; a round discovers it from the digests. Every later read of
    // either copy is pinned to the incarnation its digests came from.
    private async Task<int?> CompareAsync(
        TenantContext database, ClusterMember peer, bool force, CancellationToken cancellationToken, Hlc? horizon = null, Ulid? incarnation = null)
    {
        // Taken before either digest: whatever the peer held when this began is held here once the comparison completes.
        var began = _time.GetTimestamp();
        if (await _states.DigestAsync(database, expected: null, cancellationToken).ConfigureAwait(false) is not { } local)
        {
            return null;
        }

        var ours = local.Buckets;
        using var channel = PeerChannel.Create(new Uri(peer.PeerAddress), _credential);
        var client = new V1.Replication.ReplicationClient(channel);
        // gRPC evaluates absolute deadlines against the real clock, independently of scheduling and HLC clocks.
        var reply = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, incarnation) },
            deadline: TimeProvider.System.GetUtcNow().UtcDateTime + DigestTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Held)
        {
            // Nothing to compare with: a round has nothing to repair, but a refill covered nothing.
            return force ? null : 0;
        }

        var source = ChangeGroups.Incarnation(new V1.Database { Incarnation = reply.Incarnation })
            ?? throw new InvalidDataException("a peer's digests name the incarnation they were read from");
        if (incarnation is { } named && source != named)
        {
            // The peer answered for another copy than the refill asked for: that refill certifies nothing.
            return null;
        }

        var theirs = ApplicationEnvelope.Decode(reply.Buckets.Span, DigestBytes);
        if (theirs.Length != DigestBytes)
        {
            throw new InvalidDataException("a peer's digests are not 4096 buckets of 16 bytes");
        }

        var differing = Enumerable.Range(0, 4096).Where(bucket => !ours.AsSpan(bucket * 16, 16).SequenceEqual(theirs.AsSpan(bucket * 16, 16))).ToHashSet();
        if (differing.Count == 0 && !force && peer.Region != _membership.Self.Region)
        {
            // A round's peer is of another placed region: it holds every value of the snapshot compared, which proves the
            // shape frontier past commits its walk stops at for good (a repair, an origin of a past epoch).
            _ = await _states.MatchedAsync(database, local, cancellationToken).ConfigureAwait(false);
        }

        var compared = _differed.TryGetValue((database, peer.Name), out var previous);
        _differed[(database, peer.Name)] = (differing, began, local.Incarnation, source);
        if (database != ChangeGroups.System && (differing.Count > 0 || (compared && previous.Buckets.Count > 0)))
        {
            // Only a round that differs, or the first that matches again, goes to the analytics: idle rounds would rewrite
            // every database's usage each minute.
            Received(database, peer, reply.CalculateSize());
            _usage.RecordReplicationState(database, peer.Region, remaining: differing.Count, diverged: differing.Count > 0);
        }

        HashSet<int> repair = force ? differing : compared ? [.. differing.Where(previous.Buckets.Contains)] : [];
        // A bucket left for the next round differs only since the last one began, when it matched: held as of then, if
        // that round compared the same two copies.
        long? held = repair.Count == differing.Count ? began
            : compared && previous.Local == local.Incarnation && previous.Source == source ? previous.At : null;
        if (repair.Count == 0)
        {
            // Covered only if the copy compared is still the one in place: a copy recreated meanwhile was never compared.
            if (!await LocalHeldAsync(database, local.Incarnation, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            CatalogHeld(database, peer, held, local.Incarnation);
            return 0;
        }

        var request = new BucketRequest { Database = ChangeGroups.Wire(database, source) };
        request.Buckets.AddRange(repair.Order().Select(bucket => (uint)bucket));
        using var call = client.ReadBucket(request, new Metadata { { RegionHeader, _membership.Self.Region } },
            TimeProvider.System.GetUtcNow().UtcDateTime + ReadTimeout, cancellationToken);
        var changed = 0;
        var theirKeys = horizon is not null ? new HashSet<(string Collection, string Id)>() : null;
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            Received(database, peer, call.ResponseStream.Current.CalculateSize());
            var states = Decode(database, call.ResponseStream.Current, source);
            theirKeys?.UnionWith(states.Select(state => (state.Collection, state.Id)));
            if (await _sink.RepairAsync(database, local.Incarnation, states, cancellationToken).ConfigureAwait(false) is not { } repaired)
            {
                return null;
            }

            changed += repaired;
        }

        if (horizon is { } collected)
        {
            // shortcut: keeps the peer's keys of every repaired bucket in memory; page the comparison per bucket for databases of millions of documents.
            List<(string Collection, string Id)> lacking = [];
            BucketCursor? after = null;
            do
            {
                // Pinned to the copy whose digests chose the buckets: a copy replaced meanwhile certifies nothing.
                if (await _states.ReadBucketsAsync(database, local.Incarnation, [.. repair], after, MaxPageStates, ChangeGroups.MaxBatchBytes, cancellationToken)
                    .ConfigureAwait(false) is not (var page, var next))
                {
                    return null;
                }

                after = next;
                lacking.AddRange(page.Where(state => !theirKeys!.Contains((state.Collection, state.Id)) && state.State.MaxStamp.Value < collected.Value)
                    .Select(state => (state.Collection, state.Id)));
            }
            while (after is not null);
            if (lacking.Count > 0)
            {
                if (await _states.ForgetAsync(database, local.Incarnation, lacking, collected, cancellationToken).ConfigureAwait(false) is not { } forgotten)
                {
                    return null;
                }

                changed += forgotten;
            }
        }

        // The repairs joined into whatever copy was in place: they cover only the one compared, if it still is.
        if (!await LocalHeldAsync(database, local.Incarnation, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        _repairs.Add(changed, new KeyValuePair<string, object?>("peer_region", peer.Region));
        CatalogHeld(database, peer, held, local.Incarnation);
        return changed;
    }

    // A client database's comparison traffic goes to its replication analytics, never to its billed operations.
    private void Received(TenantContext database, ClusterMember peer, long bytes)
    {
        if (database != ChangeGroups.System)
        {
            _usage.RecordReplicationBytes(database, peer.Region, "anti_entropy", sent: false, bytes);
        }
    }

    // Whether this machine's copy in place is still `incarnation`.
    private async Task<bool> LocalHeldAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken) =>
        await _states.ReadBucketsAsync(database, incarnation, [], null, 1, 1, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>The LZ4 envelope of a page of states of <paramref name="incarnation"/> of the source's copy.</summary>
    internal static StatePage Encode(IReadOnlyList<RepairState> states, Ulid incarnation)
    {
        var list = new StateList();
        list.States.AddRange(states.Select(state => new StateEntry
        {
            Collection = state.Collection,
            Id = state.Id,
            State = UnsafeByteOperations.UnsafeWrap(DocumentStateEncoding.Encode(state.State)),
        }));
        return new StatePage
        {
            States = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(list)),
            Incarnation = ByteString.CopyFrom(incarnation.ToByteArray()),
        };
    }

    // A page from a peer, checked: decoding is a trust boundary, as for change groups. It must name the source incarnation
    // the read is pinned to.
    internal static List<RepairState> Decode(TenantContext database, StatePage page, Ulid source)
    {
        if (ChangeGroups.Incarnation(new V1.Database { Incarnation = page.Incarnation }) != source)
        {
            throw new InvalidDataException("a peer's page names the incarnation the read is pinned to");
        }

        StateList list;
        try
        {
            var bytes = ApplicationEnvelope.Decode(page.States.Span, ReplicationProtocol.MaxApplicationBytes);
            // Bound object materialization even when a peer fills the envelope with tiny entries.
            using var payload = new MemoryStream(bytes, writable: false);
            // SkipLastField counts group depth one earlier than the parser's default limit of 100.
            using var input = CodedInputStream.CreateWithLimits(payload, bytes.Length, recursionLimit: 101);
            var count = 0;
            while (input.ReadTag() is var tag && tag != 0)
            {
                if (tag == 10 && ++count > MaxPageStates)
                {
                    throw new InvalidDataException("a peer's page holds at most 1024 states");
                }

                input.SkipLastField();
            }

            list = StateList.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException error)
        {
            throw new InvalidDataException("a peer's page is not a state list", error);
        }

        if (list.States.Count > MaxPageStates)
        {
            throw new InvalidDataException("a peer's page holds at most 1024 states");
        }

        return [.. list.States.Select(entry =>
            ChangeGroups.ValidKey(database, entry.Collection, entry.Id)
                ? new RepairState(entry.Collection, entry.Id, DocumentStateEncoding.Decode(entry.State.Span))
                : throw new InvalidDataException("a repaired state names a valid collection and an id of 1 to 1024 characters"))];
    }
}
