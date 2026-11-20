using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraBase.Features.Platform;
using TerraBase.Configuration;
using TerraBase.Engine.Revisions;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed record RegionalPosition(string Organization, string Project, string Database, string Home, string Revision, bool Fenced = false);
internal sealed record RegionalPrepare(string Source, IReadOnlyList<RegionalPosition> Positions, long Epoch, RegionalCatalogSnapshot? Catalog = null, string? Operation = null, RetainedManifest? Retained = null);
internal sealed record UnservedDatabase(string DatabaseId, string Region, string Node, long Epoch, string Cause);

internal sealed record FencedDatabase(string DatabaseId, string Reason, long Epoch, string Detail);

internal sealed record RegionalStatus(string Node, string? Owner, long Epoch, bool Writer, bool Ready, string Session = "",
    IReadOnlyList<UnservedDatabase>? Unserved = null, IReadOnlyList<KeptCopy>? Kept = null, IReadOnlyList<FencedDatabase>? Fenced = null, string? Unready = null,
    IReadOnlyList<string>? PrimedWithout = null);
internal sealed record RegionalDrain(string Target);

internal readonly union RegionalHandoffResult(Done, Unavailable);
internal readonly union RegionalPrimeResult(PrimedWithout, Unavailable);
internal sealed record PrimedWithout(IReadOnlyList<string> Databases);
internal readonly union RegionalAdmitResult(RegionalAdmission, Unavailable);
internal sealed record RegionalAdmission(string Transfer);
internal readonly union DrainReservation(NatsKVEntry<byte[]>, Unavailable);

internal sealed partial class RegionalHandoff(
    TerraBaseOptions options, RegionalOwners owners, HomeLeaseGate gate, InternalCredential credential,
    IServiceProvider services, IMeterFactory meters, ILogger<RegionalHandoff> logger) : BackgroundService
{
    internal const string Bucket = "terrabase-region-owners";
    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly Lock _primeGate = new();
    private PrimeMark? _primeMark;
    private IReadOnlyList<string> _primedWithout = [];
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly Dictionary<string, TenantContext> _promoted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _prepared = new(StringComparer.Ordinal);
    private long _activeEpoch;
    private int _retiredSource;
    private long _activatingEpoch;
    private TenantContext? _activationAfter;
    private string? _activationRetainedAfter;
    private INatsKVStore? _kv;
    private NatsConnection? _nats;
    private RegionalWriterLease? _writerLease;
    private readonly string _session = Guid.NewGuid().ToString("N");

    internal async Task<RegionalStatus> StatusAsync(string? region, CancellationToken cancellationToken)
    {
        var unready = await services.GetRequiredService<Health.INodeReadiness>().UnreadyAsync(cancellationToken).ConfigureAwait(false);
        return new(options.MachineId, owners.Find(region ?? options.Region)?.Node,
            owners.Find(region ?? options.Region)?.Epoch ?? 0, (region is null || region == options.Region) && owners.Writer,
            unready is null, _session,
            services.GetRequiredService<HomeLeases>().Unserved, services.GetRequiredService<KeptCopies>().List(),
            region is null || region == options.Region ? Fenced : [],
            unready is { } condition ? Health.ReadinessConditions.Label(condition) : null, Volatile.Read(ref _primedWithout));
    }

    private IReadOnlyList<FencedDatabase> Fenced =>
        [.. _fenced.Values.Select(fenced => fenced.Status).OrderBy(fenced => fenced.DatabaseId, StringComparer.Ordinal)];

    private IEnumerable<Measurement<int>> FencedMeasurements() => _fenced.Values.GroupBy(fenced => fenced.Status.Reason, StringComparer.Ordinal)
        .Select(reason => new Measurement<int>(reason.Count(), new KeyValuePair<string, object?>("reason", reason.Key)));

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        gate.RegionalWriter = () => owners.Writer;
        gate.RegionalServing = database => PlatformDatabases.IsPlatformDatabase(database) || owners.Serving(Volatile.Read(ref _activeEpoch));
        gate.RegionalSource = database => PlatformDatabases.IsPlatformDatabase(database)
            || owners.Serving(Volatile.Read(ref _activeEpoch)) || Volatile.Read(ref _retiredSource) == 1 && gate.Proven(database);
        _ = meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.regional.fenced", FencedMeasurements, "{database}",
            "Databases homed in this node's region that refuse writes because the writer's activation check cannot prove their local data holds every acknowledged write, by reason; the rollout status names them.");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options is not { NatsUrl: { } url, NatsToken: { } token, PeersFile: not null })
        {
            return;
        }

        var connectionOptions = new NatsOpts
        {
            Url = url.ToString(),
            AuthOpts = new NatsAuthOpts { Token = token },
            Name = $"terrabase-regional-owner-{options.MachineId}",
            MaxReconnectRetry = -1,
            ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
            ReconnectWaitMax = TimeSpan.FromSeconds(1),
        };
        _nats = new NatsConnection(connectionOptions);
        var js = new NatsJSContext(_nats);
        Task? observations = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_kv is null || _writerLease is null)
                    {
                        await _nats.ConnectAsync().AsTask().WaitAsync(stoppingToken).ConfigureAwait(false);
                        _kv ??= await new NatsKVContext(js).CreateOrUpdateStoreAsync(new NatsKVConfig(Bucket)
                        {
                            History = 1,
                            Storage = NatsKVStorageType.File,
                            NumberOfReplicas = _nats.ServerInfo?.Cluster is { Length: > 0 } ? 3 : 1,
                        }, stoppingToken).ConfigureAwait(false);
                        if (_writerLease is null)
                        {
                            var writers = await new NatsKVContext(js).CreateOrUpdateStoreAsync(new NatsKVConfig(RegionalWriterLease.Bucket)
                            {
                                History = 1,
                                Storage = NatsKVStorageType.File,
                                NumberOfReplicas = _nats.ServerInfo?.Cluster is { Length: > 0 } ? 3 : 1,
                                LimitMarkerTTL = RegionalWriterLease.Ttl,
                            }, stoppingToken).ConfigureAwait(false);
                            _writerLease = new RegionalWriterLease(js, writers, options.Region, options.MachineId, _session);
                        }
                    }
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    attempt.CancelAfter(AttemptBudget);
                    await RefreshAsync(_kv, attempt.Token).ConfigureAwait(false);
                    if (_renewalFailure is not null)
                    {
                        _renewalFailure = null;
                        LogRenewalRecovered(logger, options.Region);
                    }
                    observations ??= ObserveRegionsAsync(connectionOptions, stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    RenewalFailed(error);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (observations is not null)
            {
                await observations.ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> ActivateAsync(RegionalOwner owner, CancellationToken cancellationToken)
    {
        if (_activeEpoch == owner.Epoch)
        {
            return true;
        }
        var source = owner.Previous ?? options.MachineId;
        var leases = services.GetRequiredService<HomeLeases>();
        if (!leases.Ready)
        {
            return false;
        }
        var catalog = services.GetRequiredService<IReplicaCatalog>();
        if (_activatingEpoch != owner.Epoch)
        {
            Volatile.Write(ref _retiredSource, 0);
            gate.BeginActivation();
            _activatingEpoch = owner.Epoch;
            _activationAfter = null;
            _activationRetainedAfter = null;
            _checkedAfter = null;
            _checked = false;
            _pageCaughtUp = false;
            _walkFenced.Clear();
        }
        if (!_checked && !await LocalDataCurrentAsync(owner, catalog, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        do
        {
            if (await catalog.ListHomedAsync(options.Region, _activationAfter, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
            {
                return false;
            }
            foreach (var database in page.Databases)
            {
                if (!_fenced.ContainsKey(database.DatabaseId!) && await leases.TakeRegionalAsync(database, source, cancellationToken).ConfigureAwait(false) == HomeLeases.RegionalLeaseResult.Pending)
                {
                    return false;
                }
                _activationAfter = database;
            }
            if (page.Next is null)
            {
                break;
            }
            _activationAfter = page.Next;
        }
        while (true);
        do
        {
            var retained = await RegionalRetention.RecordsAsync(services.GetRequiredService<IStorageEngine>(), _activationRetainedAfter, cancellationToken).ConfigureAwait(false);
            foreach (var stored in retained.Records)
            {
                var record = RegionalRetention.Read(stored);
                if (!await leases.TakeRetainedRegionalAsync(record, source, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                _activationRetainedAfter = stored.Id;
            }
            if (!retained.HasMore)
            {
                break;
            }
        }
        while (true);
        Volatile.Write(ref _activeEpoch, owner.Epoch);
        _activatingEpoch = 0;
        _activationAfter = null;
        _activationRetainedAfter = null;
        return true;
    }

    private void Observe(RegionalOwner owner)
    {
        var previous = owners.Find(owner.Region);
        var changed = previous != owner;
        if (owner.Region == options.Region && (previous?.Node != owner.Node || previous.Epoch != owner.Epoch))
        {
            if (owner.Node == options.MachineId)
            {
                Volatile.Write(ref _retiredSource, 0);
            }
            if (previous is not null && previous.Node == options.MachineId && owner.Node != options.MachineId
                && Volatile.Read(ref _activeEpoch) == previous.Epoch)
            {
                Volatile.Write(ref _retiredSource, 1);
            }
            Volatile.Write(ref _activeEpoch, 0);
        }
        owners.Observe(owner);
        if (owner.Region == options.Region && owner.Node == options.MachineId)
        {
            owners.Resume();
        }
        if (!changed)
        {
            if (owner.Region == options.Region && owner.Node != options.MachineId && owner.Transfer is null or { Phase: Admitted })
            {
                ResumePromoted();
            }
            return;
        }

        var replication = services.GetRequiredService<RegionReplication>();
        replication.WakeCatalog(owner.Region);
        if (owner.Region != options.Region)
        {
            return;
        }

        if (owner.Node == options.MachineId)
        {
            _prepared.Clear();
            owners.Resume();
        }
        else
        {
            Volatile.Write(ref _activeEpoch, 0);
            _prepared.Clear();
            foreach (var database in _fenced.Keys)
            {
                gate.Retire(database);
                _fenced.TryRemove(database, out _);
            }
            _walkFenced.Clear();
            ResumePromoted();
        }
    }

    private void ResumePromoted()
    {
        var replication = services.GetRequiredService<RegionReplication>();
        foreach (var database in _promoted.Values)
        {
            replication.Resume(database);
        }
        _promoted.Clear();
    }

    internal async Task<RegionalPrimeResult> PrimeAsync(string source, CancellationToken cancellationToken)
    {
        if (owners.Find(options.Region) is not { Node: var writer, Epoch: var epoch } || writer != source || source == options.MachineId)
        {
            return PrimeRefused(source, "the source is not this region's writer", "prime_source_not_writer");
        }

        var replication = services.GetRequiredService<RegionReplication>();
        PrimeMark mark;
        lock (_primeGate)
        {
            mark = _primeMark is { } current && current.Source == source && current.Epoch == epoch ? current : new PrimeMark(source, epoch, replication.CatchUpMark);
            _primeMark = mark;
        }

        IReadOnlyList<FollowedReplica> waiting = [];
        IReadOnlyList<FollowedReplica> skipped = [];
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(DrainBudget);
        try
        {
            await replication.SyncCatalogsAsync(bounded.Token).ConfigureAwait(false);
            var leases = services.GetRequiredService<HomeLeases>();
            var following = replication.Followed();
            var exempt = await leases.ConfirmUnservedElsewhereAsync(
                following.Where(replica => replica.HomeRegion != options.Region).Select(replica => replica.Database), bounded.Token).ConfigureAwait(false);
            skipped = [.. following.Where(replica => replica.HomeRegion != options.Region && exempt.ContainsKey(replica.Database))];
            waiting = [.. following.Except(skipped)];
            while (true)
            {
                if (await replication.CatchUpSinceAsync(mark.Since, waiting, bounded.Token).ConfigureAwait(false) is [_, ..] failing)
                {
                    var (reason, details) = ReplicaFailure.Describe(failing);
                    return PrimeRefused(source, reason, "replica_unavailable", details: details);
                }
                if (await services.GetRequiredService<Health.INodeReadiness>().UnreadyAsync(bounded.Token).ConfigureAwait(false) is { } unready)
                {
                    return PrimeRefused(source, "this node is not ready: " + Health.ReadinessConditions.Label(unready), "prime_unready");
                }
                if (await ServedAgainAsync(leases, skipped, exempt, bounded.Token).ConfigureAwait(false) is not [_, ..] served)
                {
                    break;
                }
                skipped = [.. skipped.Except(served)];
                waiting = served;
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            var behind = replication.BehindSince(mark.Since, waiting);
            return PrimeRefused(source, $"{behind} replicas have not caught up since this prime began; the next prime resumes from here", "prime_incomplete", error,
                new KeyValuePair<string, string>("behind", behind.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception error) when (error is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogHandoffFailed(logger, error, "prime", source);
            return new Unavailable($"the prime failed with {error.GetType().Name} during prime with peer {source}", "prime_failed",
                Details("prime", source, new KeyValuePair<string, string>("error", error.GetType().Name)));
        }

        lock (_primeGate)
        {
            if (_primeMark == mark)
            {
                _primeMark = null;
            }
        }
        IReadOnlyList<string> without = [.. skipped.Select(replica => replica.Database).Order(StringComparer.Ordinal)];
        if (without is [_, ..])
        {
            LogPrimeSkippedUnserved(logger, source, without.Count, string.Join(',', without));
        }
        Volatile.Write(ref _primedWithout, without);
        return new PrimedWithout(without);
    }

    private static async Task<IReadOnlyList<FollowedReplica>> ServedAgainAsync(HomeLeases leases, IReadOnlyList<FollowedReplica> skipped,
        IReadOnlyDictionary<string, ulong> exempt, CancellationToken cancellationToken)
    {
        List<FollowedReplica> served = [];
        foreach (var replica in skipped)
        {
            if (!await leases.StillUnservedElsewhereAsync(replica.Database, exempt[replica.Database], cancellationToken).ConfigureAwait(false))
            {
                served.Add(replica);
            }
        }
        return served;
    }

    private Unavailable PrimeRefused(string source, string reason, string code, Exception? exception = null, params ReadOnlySpan<KeyValuePair<string, string>> details) =>
        PeerRefused("prime", source, reason, code, exception, details);

    private Unavailable VerifyRefused(string source, string reason, string code, params ReadOnlySpan<KeyValuePair<string, string>> details) =>
        PeerRefused("verify", source, reason, code, null, details);

    private Unavailable PeerRefused(string phase, string source, string reason, string code, Exception? exception, ReadOnlySpan<KeyValuePair<string, string>> details)
    {
        LogDrainRefused(logger, exception, phase, source, reason, code);
        return new Unavailable($"{reason} during {phase} with peer {source}", code, Details(phase, source, details));
    }

    private static Dictionary<string, string> Details(string phase, string peer, params ReadOnlySpan<KeyValuePair<string, string>> extra)
    {
        Dictionary<string, string> details = new(StringComparer.Ordinal) { ["phase"] = phase, ["peer"] = peer };
        foreach (var (key, value) in extra)
        {
            details[key] = value;
        }
        return details;
    }

    internal async Task<bool> PrepareAsync(RegionalPrepare request, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(DrainBudget);
        cancellationToken = bounded.Token;
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PrepareCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _changing.Release();
        }
    }

    private async Task<bool> PrepareCoreAsync(RegionalPrepare request, CancellationToken cancellationToken)
    {
        if (request.Positions.Count > 10_000 || !await PreparingAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return Refuse("preparation owner or reservation changed");
        }

        if (request.Catalog is null)
        {
            return Refuse("preparation catalog missing");
        }
        await RegionalCatalog.ImportAsync(services.GetRequiredService<IStorageEngine>(), request.Catalog, services.GetRequiredService<RegionalReceiptImports>(), cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<RegionReplication>().SyncCatalogsAsync(cancellationToken).ConfigureAwait(false);

        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        var replication = services.GetRequiredService<RegionReplication>();
        var leases = services.GetRequiredService<HomeLeases>();
        var exempt = await leases.ConfirmUnservedElsewhereAsync(
            request.Positions.Where(position => position.Home != options.Region).Select(position => position.Database), cancellationToken).ConfigureAwait(false);
        List<RegionalPosition> exempted = [];
        foreach (var position in request.Positions)
        {
            if (_prepared.GetValueOrDefault(position.Database) == position.Revision)
            {
                continue;
            }
            if (position.Home != options.Region && exempt.ContainsKey(position.Database))
            {
                exempted.Add(position);
                continue;
            }
            if (await ProveAppliedAsync(position, request, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return Refuse(refused);
            }
        }

        foreach (var position in request.Positions.Where(position => position.Home == options.Region))
        {
            var database = Database(position);
            if (_prepared.GetValueOrDefault(position.Database) == position.Revision)
            {
                continue;
            }

            await replication.SuspendAsync(database).ConfigureAwait(false);
            _promoted[position.Database] = database;
            var detached = await replicas.DetachAsync(database, cancellationToken).ConfigureAwait(false);
            if (detached is null)
            {
                return Refuse("prepared replica could not be detached");
            }

            try
            {
                var imports = services.GetRequiredService<IWalBackup>();
                await imports.PreserveDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
                await imports.ImportDatabaseAsync(database, detached, cancellationToken).ConfigureAwait(false);
                if (!position.Fenced && replicas.ArchiveSetAside(database) is > 0 and var archived)
                {
                    logger.ZLogWarning($"Regional candidate archived {archived:@Files} set-aside replica files of database {position.Database:@Database}: its primary file now covers the source's certified position {position.Revision:@SourceRevision}, the highest position acknowledged at epoch {request.Epoch:@Epoch}");
                }
                _prepared[position.Database] = position.Revision;
            }
            catch (Exception error)
            {
                logger.ZLogError(error, $"Regional prepared import failed; the detached source and prior local files are retained");
                replication.Resume(database);
                throw;
            }
            finally
            {
                replicas.CompleteDetach(detached);
            }
        }

        if (request.Retained is not null && !await ReceiveRetainedAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return Refuse("retained manifest or certified positions did not verify");
        }
        await services.GetRequiredService<HomePush.HomePush>().RebuildForPromotionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var position in await ServedAgainAsync(leases, exempted, exempt, request, "preparation", cancellationToken).ConfigureAwait(false))
        {
            if (await ProveAppliedAsync(position, request, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return Refuse(refused);
            }
        }
        if (!await PreparingAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return Refuse("preparation owner or reservation changed");
        }
        await WritePreparedAsync(_kv!, new RegionalPrepared(request.Source, options.MachineId, request.Epoch, request.Operation), cancellationToken).ConfigureAwait(false);
        return await PreparingAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PreparingAsync(RegionalPrepare request, CancellationToken cancellationToken)
    {
        if (_kv is not { } kv || request.Source == options.MachineId)
        {
            return false;
        }
        var entry = await ReadAsync(kv, options.Region, cancellationToken).ConfigureAwait(false);
        if (entry is not { Value: { } value })
        {
            return false;
        }
        var owner = Decode(value);
        if (owner.Node != request.Source || owner.Epoch != request.Epoch
            || !(owner.Transfer is { Phase: Preparing } transfer && transfer.Id == request.Operation
                || request.Operation is null && (owner.Version == 0 && owner.Transfer is null
                    || owner.Transfer is { Phase: Recovering, Source: var restored } && restored == request.Source)))
        {
            return false;
        }
        Observe(owner);
        return true;
    }

    internal async Task<RegionalHandoffResult> VerifyAsync(RegionalPrepare request, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(DrainBudget);
        cancellationToken = bounded.Token;
        if (!owners.Writer)
        {
            return VerifyRefused(request.Source, "this node is not the region's writer", "verify_not_writer");
        }
        if (request.Positions.Count > 10_000)
        {
            return VerifyRefused(request.Source, "the preparation lists more than 10000 databases", "database_bound_reached");
        }
        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        if (!nodes.Any(node => node.Name == request.Source && node.Region == options.Region && node.Name != options.MachineId))
        {
            return VerifyRefused(request.Source, $"the source is not a peer in region {options.Region} of this node's peers file", "verify_source_not_peer");
        }

        var replication = services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(cancellationToken).ConfigureAwait(false);
        var leases = services.GetRequiredService<HomeLeases>();
        var exempt = await leases.ConfirmUnservedElsewhereAsync(
            request.Positions.Where(position => position.Home != options.Region).Select(position => position.Database), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RegionalPosition> exempted = [.. request.Positions.Where(position => position.Home != options.Region && exempt.ContainsKey(position.Database))];
        if (await VerifyAppliedAsync(request, [.. request.Positions.Except(exempted)], cancellationToken).ConfigureAwait(false) is { } behind)
        {
            return behind;
        }
        if (await services.GetRequiredService<Health.INodeReadiness>().UnreadyAsync(cancellationToken).ConfigureAwait(false) is { } unready)
        {
            return VerifyRefused(request.Source, "this node is not ready: " + Health.ReadinessConditions.Label(unready), "verify_unready");
        }
        return await ServedAgainAsync(leases, exempted, exempt, request, "verification", cancellationToken).ConfigureAwait(false) is [_, ..] served
            && await VerifyAppliedAsync(request, served, cancellationToken).ConfigureAwait(false) is { } late
            ? late : new Done();
    }

    private async Task<Unavailable?> VerifyAppliedAsync(RegionalPrepare request, IReadOnlyList<RegionalPosition> positions, CancellationToken cancellationToken)
    {
        var replication = services.GetRequiredService<RegionReplication>();
        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        IReadOnlyList<TenantContext> unapplied = [.. positions
            .Where(position => position.Home != options.Region && !Applied(replicas.HeldRevision(Database(position)), position))
            .Select(Database)];
        if (await replication.CatchUpAsync(unapplied, cancellationToken).ConfigureAwait(false) is [_, ..] failing)
        {
            var (reason, details) = ReplicaFailure.Describe(failing);
            return VerifyRefused(request.Source, reason, "replica_unavailable", details);
        }

        var revisions = services.GetRequiredService<IDatabaseRevisions>();
        var behind = 0;
        foreach (var position in positions)
        {
            var database = Database(position);
            var actual = position.Home == options.Region
                ? await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false)
                : replicas.HeldRevision(database);
            behind += Applied(actual, position) ? 0 : 1;
        }
        return behind > 0
            ? VerifyRefused(request.Source, $"{behind} databases have not applied the source's position", "replica_behind",
                new KeyValuePair<string, string>("databases", behind.ToString(CultureInfo.InvariantCulture)))
            : null;
    }

    private async Task<string?> ProveAppliedAsync(RegionalPosition position, RegionalPrepare request, CancellationToken cancellationToken)
    {
        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        var database = Database(position);
        if (!DatabaseRevision.TryParse(position.Revision, out var expected))
        {
            return "invalid applied-position proof";
        }
        var held = replicas.HeldRevision(database);
        if (held is not { } current || current.Incarnation != expected.Incarnation
            || DatabaseRevision.Compare(current, expected) < 0)
        {
            logger.ZLogInformation($"Regional candidate catches up database {position.Database:@Database} from {held?.ToString() ?? "no position":@Held} to the certified {position.Revision:@SourceRevision}, epoch {request.Epoch:@Epoch}");
            if (!await services.GetRequiredService<RegionReplication>().CatchUpAsync(database, cancellationToken).ConfigureAwait(false))
            {
                return "replica follower unavailable during preparation";
            }
        }
        if (replicas.HeldRevision(database) is not { } actual || actual.Incarnation != expected.Incarnation
            || DatabaseRevision.Compare(actual, expected) < 0)
        {
            return "replica has not applied the certified source position";
        }
        logger.ZLogInformation($"Regional candidate applied proof: {request.Source:@Source} to {options.MachineId:@Target}, database {position.Database:@Database}, source {position.Revision:@SourceRevision}, target {actual.ToString():@TargetRevision}, epoch {request.Epoch:@Epoch}");
        return null;
    }

    private async Task<IReadOnlyList<RegionalPosition>> ServedAgainAsync(HomeLeases leases, IReadOnlyList<RegionalPosition> exempted,
        IReadOnlyDictionary<string, ulong> exempt, RegionalPrepare request, string phase, CancellationToken cancellationToken)
    {
        List<RegionalPosition> served = [];
        foreach (var position in exempted)
        {
            if (await leases.StillUnservedElsewhereAsync(position.Database, exempt[position.Database], cancellationToken).ConfigureAwait(false))
            {
                LogPositionUnserved(logger, phase, position.Database, position.Home, position.Revision, request.Source, request.Epoch);
            }
            else
            {
                served.Add(position);
            }
        }
        return served;
    }

    private static bool Applied(DatabaseRevision? actual, RegionalPosition position) =>
        DatabaseRevision.TryParse(position.Revision, out var expected) && actual is { } applied
        && applied.Incarnation == expected.Incarnation && DatabaseRevision.Compare(applied, expected) >= 0;

    private enum DrainPhase
    {
        Admit,
        Reserve,
        Readiness,
        Collect,
        Prepare,
        Verify,
        Prime,
        Activate,
        Observe,
        Quiesce
    }

    private sealed class DrainProgress(string peer)
    {
        internal DrainPhase Phase { get; set; } = DrainPhase.Admit;
        internal string Peer { get; set; } = peer;
        internal string Label => Phase switch
        {
            DrainPhase.Admit => "admit",
            DrainPhase.Reserve => "reserve",
            DrainPhase.Readiness => "readiness",
            DrainPhase.Collect => "collect",
            DrainPhase.Prepare => "prepare",
            DrainPhase.Verify => "verify",
            DrainPhase.Prime => "prime",
            DrainPhase.Activate => "activate",
            DrainPhase.Observe => "observe",
            DrainPhase.Quiesce => "quiesce",
            _ => throw new UnreachableException()
        };
    }

    internal Task<RegionalHandoffResult> DrainAsync(string target, string? transfer, CancellationToken cancellationToken) =>
        HandoffAsync<RegionalHandoffResult>((progress, token) => DrainCoreAsync(target, transfer, progress, token), refused => refused, cancellationToken);

    internal Task<RegionalAdmitResult> AdmitAsync(string target, string spec, CancellationToken cancellationToken) =>
        HandoffAsync<RegionalAdmitResult>((progress, token) => AdmitCoreAsync(target, spec, progress, token), refused => refused, cancellationToken);

    private async Task<T> HandoffAsync<T>(Func<DrainProgress, CancellationToken, Task<T>> run, Func<Unavailable, T> refused, CancellationToken cancellationToken)
    {
        var progress = new DrainProgress(options.MachineId);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(DrainBudget);
        try
        {
            return await run(progress, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            return refused(DrainRefused(progress, "the regional drain did not complete before its deadline", "drain_timeout", error));
        }
        catch (HttpRequestException error) when (!cancellationToken.IsCancellationRequested)
        {
            var (reason, code) = progress.Phase switch
            {
                DrainPhase.Observe => ("the observer could not be reached", "observer_unreachable"),
                DrainPhase.Readiness or DrainPhase.Prepare or DrainPhase.Verify or DrainPhase.Activate
                    => ("the target could not be reached", "target_unreachable"),
                DrainPhase.Admit or DrainPhase.Reserve or DrainPhase.Collect or DrainPhase.Prime or DrainPhase.Quiesce
                    => ("a required peer could not be reached", "peer_unreachable"),
                _ => throw new UnreachableException()
            };
            return refused(DrainRefused(progress, reason, code, error));
        }
        catch (NatsKVWrongLastRevisionException error) when (!cancellationToken.IsCancellationRequested)
        {
            return refused(DrainRefused(progress, "the regional owner changed during the drain", "owner_changed", error));
        }
        catch (NatsKVWrongLastRevisionException error)
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogHandoffFailed(logger, error, progress.Label, progress.Peer);
            return refused(new Unavailable($"the drain failed with {error.GetType().Name} during {progress.Label} with peer {progress.Peer}", "drain_failed",
                Details(progress.Label, progress.Peer, new KeyValuePair<string, string>("error", error.GetType().Name))));
        }
    }

    private sealed record AdmittedTarget(INatsKVStore Kv, ImmutableArray<ServingNode> Nodes, ServingNode Peer);

    private async Task<AdmittedTarget?> AdmitTargetAsync(string target, CancellationToken cancellationToken)
    {
        if (_kv is not { } kv)
        {
            return null;
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return nodes.FirstOrDefault(node => node.Region == options.Region && node.Name == target && target != options.MachineId) is { } peer
            ? new AdmittedTarget(kv, nodes, peer) : null;
    }

    private Unavailable TargetRefused(DrainProgress progress) => _kv is null
        ? DrainRefused(progress, "owner store unavailable", "owner_store_unavailable")
        : DrainRefused(progress, "target is outside admitted regional peers", "target_not_admitted");

    private async Task<RegionalAdmitResult> AdmitCoreAsync(string target, string spec, DrainProgress progress, CancellationToken cancellationToken)
    {
        if (await AdmitTargetAsync(target, cancellationToken).ConfigureAwait(false) is not { Kv: var kv })
        {
            return TargetRefused(progress);
        }

        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        IMemoryReservation? memory = null;
        try
        {
            progress.Phase = DrainPhase.Reserve;
            if (await HeldAdmissionAsync(kv, target, spec, cancellationToken).ConfigureAwait(false) is { } held)
            {
                return new RegionalAdmission(held);
            }

            ReleaseAdmission(null);
            memory = services.GetRequiredService<IMemoryBudget>().TryReserve(DrainMemory, tenant: null);
            if (memory is null)
            {
                return DrainRefused(progress, "bulk-memory reservation unavailable", "memory_unavailable");
            }

            var reserved = await BeginDrainAsync(kv, target, Admitted, progress, cancellationToken).ConfigureAwait(false);
            if (reserved is not NatsKVEntry<byte[]> entry)
            {
                return reserved switch
                {
                    Unavailable error => error,
                    NatsKVEntry<byte[]> => throw new UnreachableException()
                };
            }

            var transfer = Decode(entry.Value!).Transfer!.Id;
            HoldAdmission(transfer, target, spec, memory);
            memory = null;
            logger.ZLogInformation($"Regional drain admitted: {options.MachineId:@Source} to {target:@Target}, transfer {transfer:@Transfer}");
            return new RegionalAdmission(transfer);
        }
        finally
        {
            memory?.Dispose();
            _changing.Release();
        }
    }

    private async Task<RegionalHandoffResult> DrainCoreAsync(string target, string? admitted, DrainProgress progress, CancellationToken cancellationToken)
    {
        if (await AdmitTargetAsync(target, cancellationToken).ConfigureAwait(false) is not (var kv, var nodes, var peer))
        {
            return TargetRefused(progress);
        }

        IDisposable? admission = null;
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress.Phase = DrainPhase.Reserve;
            if (admitted is null)
            {
                ReleaseAdmission(null);
            }

            using var reservation = admitted is null
                ? services.GetRequiredService<IMemoryBudget>().TryReserve(DrainMemory, tenant: null)
                : TakeAdmission(admitted, target);
            if (reservation is null)
            {
                return admitted is null
                    ? DrainRefused(progress, "bulk-memory reservation unavailable", "memory_unavailable")
                    : DrainRefused(progress, "the admitted transfer is unknown or expired", "admission_unknown");
            }

            var reserved = admitted is null
                ? await BeginDrainAsync(kv, target, Preparing, progress, cancellationToken).ConfigureAwait(false)
                : await ClaimAdmittedAsync(kv, target, admitted, progress, cancellationToken).ConfigureAwait(false);
            if (reserved is not NatsKVEntry<byte[]> entry)
            {
                return reserved switch
                {
                    Unavailable error => error,
                    NatsKVEntry<byte[]> => throw new UnreachableException()
                };
            }

            var owner = Decode(entry.Value!);
            var transfer = owner.Transfer!;
            var promoting = owner.Node == options.MachineId;
            if (!promoting && owner.Node != target)
            {
                return DrainRefused(progress, "regional owner changed", "owner_changed");
            }

            progress.Phase = DrainPhase.Quiesce;
            admission = await services.GetRequiredService<RegionalIngress>().HoldAsync(cancellationToken).ConfigureAwait(false);
            owners.Drain();
            owners.BeginHandoff();
            logger.ZLogInformation($"Regional handoff source fenced: {options.MachineId:@Source} to {target:@Target}, epoch {owner.Epoch:@Epoch}");
            progress.Phase = DrainPhase.Collect;
            var catalog = services.GetRequiredService<IReplicaCatalog>();
            var revisions = services.GetRequiredService<IDatabaseRevisions>();
            var replicas = services.GetRequiredService<IDatabaseReplicas>();
            var replication = services.GetRequiredService<RegionReplication>();
            var positions = new List<RegionalPosition>();
            foreach (var region in nodes.Select(node => node.Region).Distinct(StringComparer.Ordinal))
            {
                TenantContext? after = null;
                do
                {
                    if (await catalog.ListHomedAsync(region, after, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
                    {
                        return DrainRefused(progress, "regional catalog unavailable", "catalog_unavailable");
                    }

                    foreach (var database in page.Databases)
                    {
                        if (positions.Count >= 10_000)
                        {
                            return DrainRefused(progress, "regional database bound reached", "database_bound_reached");
                        }

                        DatabaseRevision? position;
                        if (region == options.Region && promoting)
                        {
                            _ = await revisions.DrainWritersAsync(database, cancellationToken).ConfigureAwait(false);
                            position = await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await replication.SuspendAsync(database).ConfigureAwait(false);
                            _promoted[database.DatabaseId!] = database;
                            position = replicas.HeldRevision(database);
                        }

                        if (region == options.Region && position is null)
                        {
                            return DrainRefused(progress, "source applied position unavailable", "position_unavailable");
                        }

                        if (position is { } applied)
                        {
                            positions.Add(new RegionalPosition(database.OrganizationId, database.ProjectId!, database.DatabaseId!, region, applied.ToString(), _fenced.ContainsKey(database.DatabaseId!)));
                        }
                    }

                    after = page.Next;
                }
                while (after is not null);
            }

            var address = peer.InternalListener(options.InternalPort);
            RegionalCatalogSnapshot? exported = null;
            if (promoting)
            {
                switch (await RegionalCatalog.ExportAsync(services.GetRequiredService<IStorageEngine>(), cancellationToken).ConfigureAwait(false))
                {
                    case RegionalCatalogSnapshot snapshot:
                        exported = snapshot;
                        break;
                    case Unavailable:
                        return DrainRefused(progress, "regional catalog exceeds the handoff bound", "catalog_unavailable");
                }
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(new RegionalPrepare(options.MachineId, positions, owner.Epoch, exported, transfer.Id), ReplicasJsonContext.Default.RegionalPrepare);
            if (payload.Length > 4 * 1024 * 1024)
            {
                return DrainRefused(progress, "preparation payload bound reached", "payload_bound_reached");
            }
            using var body = new ByteArrayContent(payload);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            progress.Phase = promoting ? DrainPhase.Prepare : DrainPhase.Verify;
            progress.Peer = target;
            using var response = await _http.PostAsync(new Uri(address, promoting ? "/v1/rollout/prepare" : "/v1/rollout/verify"), body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await TargetRefusedAsync(progress, response, cancellationToken).ConfigureAwait(false);
            }

            progress.Phase = DrainPhase.Reserve;
            progress.Peer = options.MachineId;
            if (!promoting)
            {
                var retired = owner with { Transfer = transfer with { Phase = Retiring } };
                _ = await kv.UpdateAsync(options.Region, Encode(retired), entry.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new Done();
            }

            await _writerLease!.ReleaseDrainedAsync(cancellationToken).ConfigureAwait(false);
            var next = new RegionalOwner(options.Region, target, owner.Epoch + 1, options.MachineId,
                Transfer: transfer with { Phase = Activating, Deadline = DateTimeOffset.UtcNow + ActivationBudget }, Version: ProtocolVersion);
            _ = await kv.UpdateAsync(options.Region, Encode(next), entry.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            Observe(next);
            progress.Phase = DrainPhase.Activate;
            progress.Peer = target;
            using (var activation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                activation.CancelAfter(ActivationBudget);
                await AwaitStatusAsync(new Uri(address, "/v1/rollout/status"),
                    status => status is { Writer: true, Ready: true, Owner: var active } && active == target, activation.Token).ConfigureAwait(false);
            }
            foreach (var observer in nodes.Where(node => node.Name != options.MachineId))
            {
                progress.Phase = DrainPhase.Observe;
                progress.Peer = observer.Name;
                var observedAt = observer.InternalListener(options.InternalPort);
                await AwaitStatusAsync(new Uri(observedAt, "/v1/rollout/status?region=" + options.Region),
                    status => status.Node == observer.Name && status.Owner == target && status.Epoch >= next.Epoch, cancellationToken).ConfigureAwait(false);
            }
            admission.Dispose();
            admission = null;
            progress.Phase = DrainPhase.Quiesce;
            progress.Peer = options.MachineId;
            await services.GetRequiredService<RegionalIngress>().WaitIdleAsync(cancellationToken).ConfigureAwait(false);
            logger.ZLogInformation($"Regional writer moved to {target:@Node} at epoch {next.Epoch:@Epoch} after {positions.Count:@Databases} applied positions were proven");
            return new Done();
        }
        finally
        {
            try
            {
                await RecoverUncommittedAsync(kv).ConfigureAwait(false);
            }
            finally
            {
                owners.EndHandoff();
                admission?.Dispose();
                _changing.Release();
            }
        }
    }

    private async Task<Unavailable> TargetRefusedAsync(DrainProgress progress, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json"
            || await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.DetailedErrorBody, cancellationToken).ConfigureAwait(false)
                is not { Error: { Code: { } code, Message: { } message, Details: { } refused } })
        {
            return DrainRefused(progress, "target rejected applied-position preparation", "target_rejected");
        }
        LogDrainRefused(logger, null, progress.Label, progress.Peer, message, code);
        Dictionary<string, string> details = new(refused, StringComparer.Ordinal) { ["phase"] = progress.Label, ["peer"] = progress.Peer };
        return new Unavailable("the target refused: " + message, code, details);
    }

    private Unavailable DrainRefused(DrainProgress progress, string reason, string code, Exception? exception = null)
    {
        LogDrainRefused(logger, exception, progress.Label, progress.Peer, reason, code);
        return new Unavailable($"{reason} during {progress.Label} with peer {progress.Peer}", code, Details(progress.Label, progress.Peer));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Regional handoff failed during {Phase} with peer {Peer}")]
    private static partial void LogHandoffFailed(ILogger logger, Exception exception, string phase, string peer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Regional {Phase} from {Source} at epoch {Epoch} does not wait for database {Database} at {Revision}: its home in {Home} is unserved")]
    private static partial void LogPositionUnserved(ILogger logger, string phase, string database, string home, string revision, string source, long epoch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Regional prime against {Source} completed without {Count} replicas whose home in another region is unserved: {Databases}")]
    private static partial void LogPrimeSkippedUnserved(ILogger logger, string source, int count, string databases);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Regional handoff refused during {Phase} with peer {Peer}: {Reason} ({Code})")]
    private static partial void LogDrainRefused(ILogger logger, Exception? exception, string phase, string peer, string reason, string code);

    private bool Refuse(string reason)
    {
        logger.ZLogWarning($"Regional handoff refused: {reason:@Reason}");
        return false;
    }

    private static TenantContext Database(RegionalPosition position) => new(position.Organization, PlanTier.Hobby, position.Project, position.Database);

    private static byte[] Encode(RegionalOwner owner) => JsonSerializer.SerializeToUtf8Bytes(owner, ReplicasJsonContext.Default.RegionalOwner);

    private static RegionalOwner Decode(byte[] value)
    {
        try
        {
            return JsonSerializer.Deserialize(value, ReplicasJsonContext.Default.RegionalOwner)
                ?? throw new OwnershipRecordException("record_invalid", value.Length);
        }
        catch (JsonException)
        {
            throw new OwnershipRecordException("record_invalid", value.Length);
        }
    }

    private async Task<ServingNode?> SelfRegionPeerAsync(string node, CancellationToken cancellationToken)
    {
        var peers = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return peers.FirstOrDefault(peer => peer.Name == node && peer.Region == options.Region);
    }

    private async Task<RegionalStatus?> StatusAsync(Uri address, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(address, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.RegionalStatus, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task AwaitStatusAsync(Uri address, Func<RegionalStatus, bool> settled, CancellationToken cancellationToken)
    {
        while (await StatusAsync(address, cancellationToken).ConfigureAwait(false) is not { } status || !settled(status))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<NatsKVEntry<byte[]>?> ReadAsync(INatsKVStore kv, string region, CancellationToken cancellationToken)
    {
        try
        {
            return await kv.GetEntryAsync<byte[]>(region, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
        {
            return null;
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        _changing.Dispose();
        base.Dispose();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_nats is { } nats)
        {
            await nats.DisposeAsync().ConfigureAwait(false);
        }
    }
}
