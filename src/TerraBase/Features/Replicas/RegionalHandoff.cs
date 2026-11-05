using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed record RegionalPosition(string Organization, string Project, string Database, string Home, string Revision);
internal sealed record RegionalPrepare(string Source, IReadOnlyList<RegionalPosition> Positions);
internal sealed record RegionalStatus(string Node, string? Owner, long Epoch, bool Writer, bool Ready);
internal sealed record RegionalDrain(string Target);

/// <summary>
/// Planned regional ownership transfer. The edge drains first; the old engine is fenced before its final applied
/// positions are sampled. The candidate imports only proven replicas, takes the database leases, then the region CAS.
/// Failed or ambiguous preparation keeps the old process alive and fenced for a retry.
/// </summary>
internal sealed class RegionalHandoff(
    TerraBaseOptions options, RegionalOwners owners, HomeLeaseGate gate, InternalCredential credential,
    IServiceProvider services, ILogger<RegionalHandoff> logger) : BackgroundService
{
    internal const string Bucket = "terrabase-region-owners";
    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly Dictionary<string, TenantContext> _promoted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _prepared = new(StringComparer.Ordinal);
    private INatsKVStore? _kv;
    private NatsConnection? _nats;

    internal async Task<RegionalStatus> StatusAsync(CancellationToken cancellationToken) => new(options.MachineId, owners.Find(options.Region)?.Node,
        owners.Find(options.Region)?.Epoch ?? 0, owners.Writer,
        await services.GetRequiredService<Health.INodeReadiness>().IsReadyAsync(cancellationToken).ConfigureAwait(false));

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
            Url = url.ToString(), AuthOpts = new NatsAuthOpts { Token = token },
            Name = $"terrabase-regional-owner-{options.MachineId}", MaxReconnectRetry = -1,
            ReconnectWaitMin = TimeSpan.FromMilliseconds(100), ReconnectWaitMax = TimeSpan.FromSeconds(1),
        });
        var js = new NatsJSContext(_nats);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _nats.ConnectAsync().ConfigureAwait(false);
                _kv ??= await new NatsKVContext(js).CreateOrUpdateStoreAsync(new NatsKVConfig(Bucket)
                {
                    History = 1, Storage = NatsKVStorageType.File,
                    NumberOfReplicas = _nats.ServerInfo?.Cluster is { Length: > 0 } ? 3 : 1,
                }, stoppingToken).ConfigureAwait(false);
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
                if (entry is null && region == options.Region && nodes.First(node => node.Region == region).Name == options.MachineId)
                {
                    var first = new RegionalOwner(region, options.MachineId, 1);
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

                    var changed = owners.Find(region) != owner;
                    owners.Observe(owner);
                    if (changed)
                    {
                        services.GetRequiredService<RegionReplication>().WakeCatalog(region);
                        if (region == options.Region && owner.Node == options.MachineId)
                        {
                            _prepared.Clear();
                            owners.Resume();
                        }
                        else if (region == options.Region)
                        {
                            foreach (var database in _promoted.Values)
                            {
                                services.GetRequiredService<RegionReplication>().Resume(database);
                            }
                            _promoted.Clear();
                        }
                    }
                    if (region == options.Region && owner.Node == options.MachineId)
                    {
                        // A quorum CAS, rather than a cached watch value, renews write admission.
                        _ = await kv.UpdateAsync(region, observed.Value!, observed.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
                        owners.Renew();
                    }
                }
            }
            finally
            {
                _changing.Release();
            }
        }
    }

    internal async Task<bool> PrimeAsync(string source, CancellationToken cancellationToken)
    {
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
        if (request.Positions.Count > 10_000 || !await PrimeAsync(request.Source, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        var replication = services.GetRequiredService<RegionReplication>();
        foreach (var position in request.Positions)
        {
            var database = Database(position);
            if (_prepared.GetValueOrDefault(position.Database) == position.Revision)
            {
                continue;
            }

            if (!DatabaseRevision.TryParse(position.Revision, out var expected)
                || !await replication.CatchUpAsync(database, cancellationToken).ConfigureAwait(false)
                || replicas.HeldRevision(database) is not { } actual || actual.Incarnation != expected.Incarnation
                || DatabaseRevision.Compare(actual, expected) < 0)
            {
                return false;
            }
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
                return false;
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
        foreach (var position in request.Positions.Where(position => position.Home == options.Region))
        {
            if (!await services.GetRequiredService<HomeLeases>().TakeRegionalAsync(Database(position), request.Source, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    internal async Task<bool> VerifyAsync(RegionalPrepare request, CancellationToken cancellationToken)
    {
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
        var kv = _kv;
        if (kv is null)
        {
            return false;
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        var peer = nodes.FirstOrDefault(node => node.Region == options.Region && node.Name == target && target != options.MachineId);
        if (peer is null)
        {
            return false;
        }

        await _changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await ReadAsync(kv, options.Region, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            var owner = Decode(entry.Value.Value!);
            var promoting = owner.Node == options.MachineId;
            if (!promoting && owner.Node != target)
            {
                return false;
            }

            owners.Drain();
            var catalog = services.GetRequiredService<IReplicaCatalog>();
            var revisions = services.GetRequiredService<IDatabaseRevisions>();
            var replicas = services.GetRequiredService<IDatabaseReplicas>();
            var positions = new List<RegionalPosition>();
            foreach (var region in nodes.Select(node => node.Region).Distinct(StringComparer.Ordinal))
            {
                TenantContext? after = null;
                do
                {
                    if (await catalog.ListHomedAsync(region, after, cancellationToken).ConfigureAwait(false) is not TenantDatabasePage page)
                    {
                        return false;
                    }

                    foreach (var database in page.Databases)
                    {
                        if (positions.Count >= 10_000)
                        {
                            return false;
                        }

                        DatabaseRevision? position;
                        if (region == options.Region && promoting)
                        {
                            _ = await revisions.DrainWritersAsync(database, cancellationToken).ConfigureAwait(false);
                            position = await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            position = replicas.HeldRevision(database);
                        }

                        if (region == options.Region && promoting && position is null)
                        {
                            return false;
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

            var address = new UriBuilder(Uri.UriSchemeHttp, peer.Address.ToString(), options.InternalPort).Uri;
            using var response = await _http.PostAsJsonAsync(new Uri(address, promoting ? "/v1/rollout/prepare" : "/v1/rollout/verify"),
                new RegionalPrepare(options.MachineId, positions), TerraBaseJsonContext.Default.RegionalPrepare, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            if (!promoting)
            {
                return true;
            }

            var next = new RegionalOwner(options.Region, target, owner.Epoch + 1);
            _ = await kv.UpdateAsync(options.Region, Encode(next), entry.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            owners.Observe(next);
            logger.ZLogInformation($"Regional writer moved to {target:@Node} at epoch {next.Epoch:@Epoch} after {positions.Count:@Databases} applied positions were proven");
            return true;
        }
        finally
        {
            _changing.Release();
        }
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
