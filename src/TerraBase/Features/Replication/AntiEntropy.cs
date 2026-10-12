using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Google.Protobuf;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
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

    private readonly IMembership _membership;
    private readonly IChangeGroupSource _source;
    private readonly IReplicaStates _states;
    private readonly IChangeGroupSink _sink;
    private readonly ITenantDirectory _tenants;
    private readonly TimeProvider _time;
    private readonly ILogger<AntiEntropy> _logger;
    private readonly Counter<long> _repairs;

    // Per (database, peer machine): the buckets that differed in the last round.
    private readonly ConcurrentDictionary<(TenantContext Database, string Peer), HashSet<int>> _differed = new();

    public AntiEntropy(
        IMembership membership, IChangeGroupSource source, IReplicaStates states, IChangeGroupSink sink, ITenantDirectory tenants, TimeProvider time,
        IMeterFactory meters, ILogger<AntiEntropy> logger)
    {
        (_membership, _source, _states, _sink, _tenants, _time, _logger) = (membership, source, states, sink, tenants, time, logger);
        _repairs = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.replication.repairs", "{document}",
            "Documents anti-entropy changed, by the peer region they came from.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Round, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunRoundAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One round over every database and peer; a peer that fails is tried again next round.</summary>
    internal async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        var peers = Peers(await _membership.RefreshAsync(cancellationToken).ConfigureAwait(false));
        await foreach (var database in _source.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await _tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord record)
            {
                continue;
            }

            foreach (var peer in peers.Where(peer => record.Placement.Includes(peer.Region)))
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
    /// Joins every bucket of <paramref name="database"/> that differs from the machine named <paramref name="machine"/>.
    /// Returns the documents changed, or null when that machine is not listed or this one does not hold the database.
    /// </summary>
    internal async Task<int?> RefillAsync(TenantContext database, string machine, Hlc horizon, CancellationToken cancellationToken)
    {
        var peer = Peers(await _membership.RefreshAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(member => member.Name == machine);
        return peer is null ? null : await CompareAsync(database, peer, force: true, cancellationToken, horizon).ConfigureAwait(false);
    }

    private List<ClusterMember> Peers(ClusterView view) =>
        [.. view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere).Where(member => member.Name != _membership.Self.Name && member.PeerAddress.Length > 0)];

    // Compares digests with the peer and joins the buckets to repair; null when this machine does not hold the database.
    // With a horizon (a refill), it copies: a local state of a repaired bucket that the peer lacks and that lies wholly below
    // the peer's horizon was collected there (section 11), so it goes here too instead of coming back by a later round.
    private async Task<int?> CompareAsync(TenantContext database, ClusterMember peer, bool force, CancellationToken cancellationToken, Hlc? horizon = null)
    {
        if (await _states.DigestAsync(database, cancellationToken).ConfigureAwait(false) is not { } ours)
        {
            return null;
        }

        using var channel = PeerChannel.Create(new Uri(peer.PeerAddress));
        var client = new V1.Replication.ReplicationClient(channel);
        var reply = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database) },
            deadline: _time.GetUtcNow().UtcDateTime + DigestTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Held)
        {
            return 0;
        }

        var theirs = ApplicationEnvelope.Decode(reply.Buckets.Span, ReplicationProtocol.MaxApplicationBytes);
        if (theirs.Length != DigestBytes)
        {
            throw new InvalidDataException("a peer's digests are not 4096 buckets of 16 bytes");
        }

        var differing = Enumerable.Range(0, 4096).Where(bucket => !ours.AsSpan(bucket * 16, 16).SequenceEqual(theirs.AsSpan(bucket * 16, 16))).ToHashSet();
        var previous = _differed.GetValueOrDefault((database, peer.Name)) ?? [];
        _differed[(database, peer.Name)] = differing;
        var repair = force ? differing : [.. differing.Where(previous.Contains)];
        if (repair.Count == 0)
        {
            return 0;
        }

        var request = new BucketRequest { Database = ChangeGroups.Wire(database) };
        request.Buckets.AddRange(repair.Order().Select(bucket => (uint)bucket));
        using var call = client.ReadBucket(request, deadline: _time.GetUtcNow().UtcDateTime + ReadTimeout, cancellationToken: cancellationToken);
        var changed = 0;
        var theirKeys = new HashSet<(string Collection, string Id)>();
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var states = Decode(call.ResponseStream.Current);
            theirKeys.UnionWith(states.Select(state => (state.Collection, state.Id)));
            changed += await _sink.RepairAsync(database, states, cancellationToken).ConfigureAwait(false) ?? 0;
        }

        if (horizon is { } collected)
        {
            // shortcut: keeps the peer's keys of every repaired bucket in memory; page the comparison per bucket for databases of millions of documents.
            List<(string Collection, string Id)> lacking = [];
            BucketCursor? after = null;
            do
            {
                (var local, after) = await _states.ReadBucketsAsync(database, [.. repair], after, MaxPageStates, ChangeGroups.MaxBatchBytes, cancellationToken).ConfigureAwait(false);
                lacking.AddRange(local.Where(state => !theirKeys.Contains((state.Collection, state.Id)) && state.State.MaxStamp.Value < collected.Value)
                    .Select(state => (state.Collection, state.Id)));
            }
            while (after is not null);
            changed += await _states.ForgetAsync(database, lacking, collected, cancellationToken).ConfigureAwait(false);
        }

        _repairs.Add(changed, new KeyValuePair<string, object?>("peer_region", peer.Region));
        return changed;
    }

    /// <summary>The LZ4 envelope of a page of states.</summary>
    internal static StatePage Encode(IReadOnlyList<RepairState> states)
    {
        var list = new StateList();
        list.States.AddRange(states.Select(state => new StateEntry
        {
            Collection = state.Collection,
            Id = state.Id,
            State = UnsafeByteOperations.UnsafeWrap(DocumentStateEncoding.Encode(state.State)),
        }));
        return new StatePage { States = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(list.ToByteArray())) };
    }

    // A page from a peer, checked: decoding is a trust boundary, as for change groups.
    private static List<RepairState> Decode(StatePage page)
    {
        StateList list;
        try
        {
            list = StateList.Parser.ParseFrom(ApplicationEnvelope.Decode(page.States.Span, ReplicationProtocol.MaxApplicationBytes));
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
            (DatabaseName.TryParse(entry.Collection, out _) || entry.Collection is DocumentDelta.Collections or DocumentDelta.Indexes or DocumentDelta.Unique)
            && entry.Id.Length is > 0 and <= 1024 && !entry.Id.Any(char.IsControl)
                ? new RepairState(entry.Collection, entry.Id, DocumentStateEncoding.Decode(entry.State.Span))
                : throw new InvalidDataException("a repaired state names a valid collection and an id of 1 to 1024 characters"))];
    }
}
