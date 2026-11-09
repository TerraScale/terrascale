using System.Text.Json;
using System.Net.Http.Headers;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Hosting;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed record RegionalPosition(string Organization, string Project, string Database, string Home, string Revision);
internal sealed record RegionalPrepare(string Source, IReadOnlyList<RegionalPosition> Positions, long Epoch, RegionalCatalogSnapshot? Catalog = null, string? Operation = null);
internal sealed record RegionalStatus(string Node, string? Owner, long Epoch, bool Writer, bool Ready, string Session = "");
internal sealed record RegionalDrain(string Target);

// Planned regional ownership transfer: the edge drains first; the old engine is fenced before its final applied
// positions are sampled; the candidate imports proven replicas; the regional CAS precedes database lease activation.
// An uncommitted preparation leaves database leases untouched; a quorum rollback resumes the old writer.
internal sealed partial class RegionalHandoff(
    TerraBaseOptions options, RegionalOwners owners, HomeLeaseGate gate, InternalCredential credential,
    IServiceProvider services, ILogger<RegionalHandoff> logger) : BackgroundService
{
    internal const string Bucket = "terrabase-region-owners";
    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly Dictionary<string, TenantContext> _promoted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _prepared = new(StringComparer.Ordinal);
    private long _activeEpoch;
    private long _activatingEpoch;
    private TenantContext? _activationAfter;
    private INatsKVStore? _kv;
    private NatsConnection? _nats;
    private RegionalWriterLease? _writerLease;
    private readonly string _session = Guid.NewGuid().ToString("N");

    internal async Task<RegionalStatus> StatusAsync(CancellationToken cancellationToken, string? region = null) => new(options.MachineId, owners.Find(region ?? options.Region)?.Node,
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

        _nats = new NatsConnection(new NatsOpts
        {
            Url = url.ToString(),
            AuthOpts = new NatsAuthOpts { Token = token },
            Name = $"terrabase-regional-owner-{options.MachineId}",
            MaxReconnectRetry = -1,
            ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
            ReconnectWaitMax = TimeSpan.FromSeconds(1),
        });
        var js = new NatsJSContext(_nats);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _nats.ConnectAsync().ConfigureAwait(false);
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
                await RefreshAsync(_kv, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning($"Regional ownership could not be confirmed; customer writes remain fenced after the lease expires: {error.GetType().Name:@Error}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(INatsKVStore kv, CancellationToken cancellationToken)
    {
        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var region in nodes.Select(node => node.Region).Distinct(StringComparer.Ordinal))
        {
            await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = await ReadAsync(kv, region, cancellationToken).ConfigureAwait(false);
                if (entry is null && region == options.Region)
                {
                    // Any admitted local node may publish the same deterministic initial
                    // owner. A follower can start first during the first rolling deployment.
                    var first = new RegionalOwner(region, nodes.Where(node => node.Region == region).MinBy(node => node.Name, StringComparer.Ordinal)!.Name, 1, Version: ProtocolVersion);
                    try
                    {
                        _ = await kv.CreateAsync(region, Encode(first), cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (NatsKVCreateException)
                    {
                        // Another session created it; read the winner below.
                    }

                    entry = await ReadAsync(kv, region, cancellationToken).ConfigureAwait(false);
                }

                if (entry is { } observed)
                {
                    var owner = Decode(observed.Value!);
                    if (owner.Region != region || !nodes.Any(node => node.Region == region && node.Name == owner.Node))
                    {
                        throw new InvalidDataException("regional owner is outside admitted peers");
                    }

                    if (region == options.Region)
                    {
                        _startupOwner ??= owner;
                    }

                    if (region == options.Region && await RecoverTransferAsync(kv, observed, owner, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                    Observe(owner);
                    if (region == options.Region && owner.Node == options.MachineId)
                    {
                        await RenewWriterAsync(kv, observed, owner, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _changing.Release();
            }
        }
    }

    private async Task<bool> ActivateAsync(RegionalOwner owner, CancellationToken cancellationToken)
    {
        if (_activeEpoch == owner.Epoch)
        {
            return true;
        }
        // A restarted owner must also confirm its database leases. Its node
        // session changed even when the durable regional epoch did not.
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
                // A bounded attempt may expire between database confirmations.
                // Keep progress for this epoch instead of redoing its first
                // leases forever when a region has many replicated databases.
                _activationAfter = database;
            }
            if (page.Next is null)
            {
                break;
            }
            _activationAfter = page.Next;
        }
        while (true);
        _activeEpoch = owner.Epoch;
        _activatingEpoch = 0;
        _activationAfter = null;
        return true;
    }

    private void Observe(RegionalOwner owner)
    {
        var changed = owners.Find(owner.Region) != owner;
        owners.Observe(owner);
        if (owner.Region == options.Region && owner.Node == options.MachineId)
        {
            // Refresh and recovery also call Observe for an unchanged record.
            // A previous failed drain must not leave that writer fenced forever.
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
        await RegionalCatalog.ImportAsync(services.GetRequiredService<IStorageEngine>(), request.Catalog, cancellationToken).ConfigureAwait(false);
        // An aborted preparation can leave the target's old follower suspended.
        // Reconcile the newly imported catalog before asking it to catch up:
        // the retry may include databases created after bulk priming as well.
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
            // Bulk priming happens before the edge barrier. Reuse an already
            // applied replica; only the final missing suffix needs a fenced pull.
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

        // Check every position before detaching any replica. A partially prepared retry re-pulls from the still-live source.
        var engine = services.GetRequiredService<IStorageEngine>();
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
                _ = await engine.DeleteDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
                await services.GetRequiredService<IWalBackup>().ImportDatabaseAsync(database, detached, cancellationToken).ConfigureAwait(false);
                _prepared[position.Database] = position.Revision;
            }
            catch
            {
                replication.Resume(database);
                throw;
            }
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
        // This request read the authoritative owner under the same change gate
        // as Refresh. Observe it now: the background loop may still cache the
        // abandoned activation, which otherwise leaves prior copies suspended.
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

    internal async Task<bool> DrainAsync(string target, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(DrainBudget);
        try
        {
            return await DrainCoreAsync(target, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (NatsKVWrongLastRevisionException)
        {
            return false;
        }
    }

    private async Task<bool> DrainCoreAsync(string target, CancellationToken cancellationToken)
    {
        var kv = _kv;
        if (kv is null)
        {
            return Refuse("owner store unavailable");
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        var peer = nodes.FirstOrDefault(node => node.Region == options.Region && node.Name == target && target != options.MachineId);
        if (peer is null)
        {
            return Refuse("target is outside admitted regional peers");
        }

        IDisposable? admission = null;
        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await BeginDrainAsync(kv, target, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return Refuse("stop reservation unavailable");
            }

            var owner = Decode(entry.Value.Value!);
            var transfer = owner.Transfer!;
            var promoting = owner.Node == options.MachineId;
            if (!promoting && owner.Node != target)
            {
                return Refuse("regional owner changed");
            }

            using var reservation = services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Bulk, 16L * 1024 * 1024, tenant: null);
            if (reservation is null)
            {
                return Refuse("bulk-memory reservation unavailable");
            }
            admission = await services.GetRequiredService<RegionalIngress>().HoldAsync(cancellationToken).ConfigureAwait(false);
            owners.Drain();
            logger.ZLogInformation($"Regional handoff source fenced: {options.MachineId:@Source} to {target:@Target}, epoch {owner.Epoch:@Epoch}");
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
                        return Refuse("regional catalog unavailable");
                    }

                    foreach (var database in page.Databases)
                    {
                        if (positions.Count >= 10_000)
                        {
                            return Refuse("regional database bound reached");
                        }

                        DatabaseRevision? position;
                        if (region == options.Region && promoting)
                        {
                            _ = await revisions.DrainWritersAsync(database, cancellationToken).ConfigureAwait(false);
                            position = await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // Freeze this source's applied position while its peer proves the final barrier.
                            // Resume is registered even when a failed handoff keeps this process serving.
                            await replication.SuspendAsync(database).ConfigureAwait(false);
                            _promoted[database.DatabaseId!] = database;
                            position = replicas.HeldRevision(database);
                        }

                        if (region == options.Region && position is null)
                        {
                            return Refuse("source applied position unavailable");
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
                ? await RegionalCatalog.ExportAsync(services.GetRequiredService<IStorageEngine>(), cancellationToken).ConfigureAwait(false) : null, transfer.Id), TerraBaseJsonContext.Default.RegionalPrepare);
            if (payload.Length > 4 * 1024 * 1024)
            {
                return Refuse("preparation payload bound reached");
            }
            using var body = new ByteArrayContent(payload);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.PostAsync(new Uri(address, promoting ? "/v1/rollout/prepare" : "/v1/rollout/verify"), body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Refuse("target rejected applied-position preparation");
            }

            if (!promoting)
            {
                var retired = owner with { Transfer = transfer with { Phase = Retiring } };
                _ = await kv.UpdateAsync(options.Region, Encode(retired), entry.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
                return true;
            }

            // Native writers and in-flight requests are finished before releasing
            // exclusive admission. A delayed owner watch cannot admit both nodes.
            await _writerLease!.ReleaseDrainedAsync(cancellationToken).ConfigureAwait(false);
            var next = new RegionalOwner(options.Region, target, owner.Epoch + 1, options.MachineId,
                Transfer: transfer with { Phase = Activating, Deadline = DateTimeOffset.UtcNow + ActivationBudget }, Version: ProtocolVersion);
            _ = await kv.UpdateAsync(options.Region, Encode(next), entry.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            Observe(next);
            using (var activation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                activation.CancelAfter(ActivationBudget);
                while (true)
                {
                    using var ready = await _http.GetAsync(new Uri(address, "/v1/rollout/status"), activation.Token).ConfigureAwait(false);
                    if (ready.IsSuccessStatusCode && await ready.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.RegionalStatus, activation.Token).ConfigureAwait(false)
                        is { Writer: true, Ready: true, Owner: var active } && active == target)
                    {
                        break;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(25), activation.Token).ConfigureAwait(false);
                }
            }
            // Remote forwarding uses the observed regional owner directly, outside this
            // region's edge. Do not stop its old address while an admitted peer still routes there.
            foreach (var observer in nodes.Where(node => node.Name != options.MachineId))
            {
                var observedAt = observer.InternalListener(options.InternalPort);
                while (true)
                {
                    using var seen = await _http.GetAsync(new Uri(observedAt, "/v1/rollout/status?region=" + options.Region), cancellationToken).ConfigureAwait(false);
                    if (seen.IsSuccessStatusCode && await seen.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.RegionalStatus, cancellationToken).ConfigureAwait(false)
                        is { Node: var observedNode, Owner: var observedOwner, Epoch: var observedEpoch }
                        && observedNode == observer.Name && observedOwner == target && observedEpoch >= next.Epoch)
                    {
                        break;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
                }
            }
            admission.Dispose();
            admission = null;
            await services.GetRequiredService<RegionalIngress>().WaitIdleAsync(cancellationToken).ConfigureAwait(false);
            logger.ZLogInformation($"Regional writer moved to {target:@Node} at epoch {next.Epoch:@Epoch} after {positions.Count:@Databases} applied positions were proven");
            return true;
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

    private bool Refuse(string reason)
    {
        logger.ZLogWarning($"Regional handoff refused: {reason:@Reason}");
        return false;
    }

    private static TenantContext Database(RegionalPosition position) => new(position.Organization, PlanTier.Hobby, position.Project, position.Database);

    private static byte[] Encode(RegionalOwner owner) => JsonSerializer.SerializeToUtf8Bytes(owner, TerraBaseJsonContext.Default.RegionalOwner);

    private static RegionalOwner Decode(byte[] value) => JsonSerializer.Deserialize(value, TerraBaseJsonContext.Default.RegionalOwner)
        ?? throw new InvalidDataException("invalid regional owner");

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
