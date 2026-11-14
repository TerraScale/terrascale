using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text.Json;
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

internal sealed record RegionalPosition(string Organization, string Project, string Database, string Home, string Revision);
internal sealed record RegionalPrepare(string Source, IReadOnlyList<RegionalPosition> Positions, long Epoch, RegionalCatalogSnapshot? Catalog = null, string? Operation = null, RetainedManifest? Retained = null);
internal sealed record RegionalStatus(string Node, string? Owner, long Epoch, bool Writer, bool Ready, string Session = "");
internal sealed record RegionalDrain(string Target);

internal readonly union RegionalDrainResult(Done, Unavailable);
internal readonly union DrainReservation(NatsKVEntry<byte[]>, Unavailable);

internal sealed partial class RegionalHandoff(
    TerraBaseOptions options, RegionalOwners owners, HomeLeaseGate gate, InternalCredential credential,
    IServiceProvider services, IMeterFactory meters, ILogger<RegionalHandoff> logger) : BackgroundService
{
    internal const string Bucket = "terrabase-region-owners";
    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly Dictionary<string, TenantContext> _promoted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _prepared = new(StringComparer.Ordinal);
    private long _activeEpoch;
    private long _activatingEpoch;
    private TenantContext? _activationAfter;
    private string? _activationRetainedAfter;
    private INatsKVStore? _kv;
    private NatsConnection? _nats;
    private RegionalWriterLease? _writerLease;
    private readonly string _session = Guid.NewGuid().ToString("N");

    internal async Task<RegionalStatus> StatusAsync(string? region, CancellationToken cancellationToken) => new(options.MachineId, owners.Find(region ?? options.Region)?.Node,
        owners.Find(region ?? options.Region)?.Epoch ?? 0, (region is null || region == options.Region) && owners.Writer,
        await services.GetRequiredService<Health.INodeReadiness>().IsReadyAsync(cancellationToken).ConfigureAwait(false), _session);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        gate.RegionalWriter = () => owners.Writer;
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
            _activatingEpoch = owner.Epoch;
            _activationAfter = null;
            _activationRetainedAfter = null;
        }
        do
        {
            if (await catalog.ListHomedAsync(options.Region, _activationAfter, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
            {
                return false;
            }
            foreach (var database in page.Databases)
            {
                if (!await leases.TakeRegionalAsync(database, source, cancellationToken).ConfigureAwait(false))
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
        _activeEpoch = owner.Epoch;
        _activatingEpoch = 0;
        _activationAfter = null;
        _activationRetainedAfter = null;
        return true;
    }

    private void Observe(RegionalOwner owner)
    {
        var changed = owners.Find(owner.Region) != owner;
        owners.Observe(owner);
        if (owner.Region == options.Region && owner.Node == options.MachineId)
        {
            owners.Resume();
        }
        if (!changed)
        {
            if (owner.Region == options.Region && owner.Node != options.MachineId && owner.Transfer is null)
            {
                foreach (var database in _promoted.Values)
                {
                    services.GetRequiredService<RegionReplication>().Resume(database);
                }
                _promoted.Clear();
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
            _prepared.Clear();
            foreach (var database in _promoted.Values)
            {
                replication.Resume(database);
            }
            _promoted.Clear();
        }
    }

    internal async Task<bool> PrimeAsync(string source, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(DrainBudget);
        cancellationToken = bounded.Token;
        if (owners.Find(options.Region)?.Node != source || source == options.MachineId)
        {
            return false;
        }

        var replication = services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(cancellationToken).ConfigureAwait(false);
        await replication.PullAllAsync(cancellationToken).ConfigureAwait(false);
        return await services.GetRequiredService<Health.INodeReadiness>().IsReadyAsync(cancellationToken).ConfigureAwait(false);
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
        foreach (var position in request.Positions)
        {
            var database = Database(position);
            if (_prepared.GetValueOrDefault(position.Database) == position.Revision)
            {
                continue;
            }

            if (!DatabaseRevision.TryParse(position.Revision, out var expected))
            {
                return Refuse("invalid applied-position proof");
            }
            var held = replicas.HeldRevision(database);
            if (held is not { } current || current.Incarnation != expected.Incarnation
                || DatabaseRevision.Compare(current, expected) < 0)
            {
                if (!await replication.CatchUpAsync(database, cancellationToken).ConfigureAwait(false))
                {
                    return Refuse("replica follower unavailable during preparation");
                }
            }
            if (replicas.HeldRevision(database) is not { } actual || actual.Incarnation != expected.Incarnation
                || DatabaseRevision.Compare(actual, expected) < 0)
            {
                return Refuse("replica has not applied the certified source position");
            }
            logger.ZLogInformation($"Regional candidate applied proof: {request.Source:@Source} to {options.MachineId:@Target}, database {position.Database:@Database}, source {position.Revision:@SourceRevision}, target {actual.ToString():@TargetRevision}, epoch {request.Epoch:@Epoch}");
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

    internal async Task<bool> VerifyAsync(RegionalPrepare request, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(DrainBudget);
        cancellationToken = bounded.Token;
        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        if (!owners.Writer || request.Positions.Count > 10_000
            || !nodes.Any(node => node.Name == request.Source && node.Region == options.Region && node.Name != options.MachineId))
        {
            return false;
        }

        var replication = services.GetRequiredService<RegionReplication>();
        await replication.SyncCatalogsAsync(cancellationToken).ConfigureAwait(false);
        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        var revisions = services.GetRequiredService<IDatabaseRevisions>();
        foreach (var position in request.Positions)
        {
            var database = Database(position);
            DatabaseRevision? actual;
            if (position.Home == options.Region)
            {
                actual = await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!await replication.CatchUpAsync(database, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                actual = replicas.HeldRevision(database);
            }

            if (!DatabaseRevision.TryParse(position.Revision, out var expected) || actual is not { } applied
                || applied.Incarnation != expected.Incarnation || DatabaseRevision.Compare(applied, expected) < 0)
            {
                return false;
            }
        }

        return await services.GetRequiredService<Health.INodeReadiness>().IsReadyAsync(cancellationToken).ConfigureAwait(false);
    }

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

    internal async Task<RegionalDrainResult> DrainAsync(string target, CancellationToken cancellationToken)
    {
        var progress = new DrainProgress(options.MachineId);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(DrainBudget);
        try
        {
            return await DrainCoreAsync(target, progress, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            return DrainRefused(progress, "the regional drain did not complete before its deadline", "drain_timeout", error);
        }
        catch (HttpRequestException error) when (!cancellationToken.IsCancellationRequested)
        {
            var (reason, code) = progress.Phase switch
            {
                DrainPhase.Observe => ("the observer could not be reached", "observer_unreachable"),
                DrainPhase.Readiness or DrainPhase.Prepare or DrainPhase.Verify or DrainPhase.Activate
                    => ("the target could not be reached", "target_unreachable"),
                DrainPhase.Admit or DrainPhase.Reserve or DrainPhase.Collect or DrainPhase.Prime or DrainPhase.Quiesce
                    => ("the regional drain could not reach a required peer", "drain_unavailable"),
                _ => throw new UnreachableException()
            };
            return DrainRefused(progress, reason, code, error);
        }
        catch (NatsKVWrongLastRevisionException error) when (!cancellationToken.IsCancellationRequested)
        {
            return DrainRefused(progress, "the regional owner changed during the drain", "owner_changed", error);
        }
        catch (NatsKVWrongLastRevisionException error)
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
    }

    private async Task<RegionalDrainResult> DrainCoreAsync(string target, DrainProgress progress, CancellationToken cancellationToken)
    {
        var kv = _kv;
        if (kv is null)
        {
            return DrainRefused(progress, "owner store unavailable");
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        var peer = nodes.FirstOrDefault(node => node.Region == options.Region && node.Name == target && target != options.MachineId);
        if (peer is null)
        {
            return DrainRefused(progress, "target is outside admitted regional peers");
        }

        IDisposable? admission = null;
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress.Phase = DrainPhase.Reserve;
            var reserved = await BeginDrainAsync(kv, target, progress, cancellationToken).ConfigureAwait(false);
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
                return DrainRefused(progress, "regional owner changed");
            }

            using var reservation = services.GetRequiredService<IMemoryBudget>().TryReserve(16L * 1024 * 1024, tenant: null);
            if (reservation is null)
            {
                return DrainRefused(progress, "bulk-memory reservation unavailable");
            }
            progress.Phase = DrainPhase.Quiesce;
            admission = await services.GetRequiredService<RegionalIngress>().HoldAsync(cancellationToken).ConfigureAwait(false);
            owners.Drain();
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
                        return DrainRefused(progress, "regional catalog unavailable");
                    }

                    foreach (var database in page.Databases)
                    {
                        if (positions.Count >= 10_000)
                        {
                            return DrainRefused(progress, "regional database bound reached");
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
                            return DrainRefused(progress, "source applied position unavailable");
                        }

                        if (position is { } applied)
                        {
                            positions.Add(new RegionalPosition(database.OrganizationId, database.ProjectId!, database.DatabaseId!, region, applied.ToString()));
                        }
                    }

                    after = page.Next;
                }
                while (after is not null);
            }

            var address = peer.InternalListener(options.InternalPort);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new RegionalPrepare(options.MachineId, positions, owner.Epoch, promoting
                ? await RegionalCatalog.ExportAsync(services.GetRequiredService<IStorageEngine>(), cancellationToken).ConfigureAwait(false) : null, transfer.Id), ReplicasJsonContext.Default.RegionalPrepare);
            if (payload.Length > 4 * 1024 * 1024)
            {
                return DrainRefused(progress, "preparation payload bound reached");
            }
            using var body = new ByteArrayContent(payload);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            progress.Phase = promoting ? DrainPhase.Prepare : DrainPhase.Verify;
            progress.Peer = target;
            using var response = await _http.PostAsync(new Uri(address, promoting ? "/v1/rollout/prepare" : "/v1/rollout/verify"), body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return DrainRefused(progress, "target rejected applied-position preparation");
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
                admission?.Dispose();
                _changing.Release();
            }
        }
    }

    private Unavailable DrainRefused(DrainProgress progress, string reason, string code = "drain_unavailable", Exception? exception = null)
    {
        LogDrainRefused(logger, exception, progress.Label, progress.Peer, reason, code);
        return new Unavailable($"{reason} during {progress.Label} with peer {progress.Peer}", code);
    }

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
