using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.HomePush;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed class RetainedReconciler(
    TerraBaseOptions options, RegionalOwners owners, IServiceProvider services,
    InternalCredential credential, IMeterFactory meters, TimeProvider time, ILogger<RetainedReconciler> logger) : BackgroundService
{
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private readonly ConcurrentDictionary<string, string> _current = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _leased = new(StringComparer.Ordinal);
    private readonly Lock _cacheGate = new();
    private int _catalogCount;
    private int _heldCount;
    private double _oldestMissing;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.retention.catalog", () => Volatile.Read(ref _catalogCount));
        meter.CreateObservableGauge("terrabase.retention.current", () => Volatile.Read(ref _heldCount));
        meter.CreateObservableGauge("terrabase.retention.missing", () => Math.Max(0, Volatile.Read(ref _catalogCount) - Volatile.Read(ref _heldCount)));
        meter.CreateObservableGauge("terrabase.retention.oldest_missing", () => Volatile.Read(ref _oldestMissing), "s");
        return base.StartAsync(cancellationToken);
    }

    internal async Task<RetainedCertification> CertifyAsync(CancellationToken token)
    {
        var engine = services.GetRequiredService<IStorageEngine>();
        using var reservation = services.GetRequiredService<IMemoryBudget>().TryReserve(_current.Count * 160L + RegionalRetention.PageBytes, tenant: null)
            ?? throw new StorageBusyException();
        var unseen = _current.Keys.ToHashSet(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        var missing = 0;
        var oldest = 0d;
        string? after = null;
        do
        {
            var page = await RegionalRetention.RecordsAsync(engine, after, token).ConfigureAwait(false);
            foreach (var stored in page.Records)
            {
                var record = RegionalRetention.Read(stored);
                _ = unseen.Remove(stored.Id);
                RegionalRetention.Append(hash, stored.Id, record.Revision);
                count++;
                if (_current.GetValueOrDefault(stored.Id) != record.Revision || !engine.HasDatabaseFiles(record.Storage, token))
                {
                    missing++;
                    oldest = Math.Max(oldest, (time.GetUtcNow() - record.DeletedAt).TotalSeconds);
                }
            }
            after = page.HasMore ? page.Records[^1].Id : null;
        }
        while (after is not null);
        foreach (var id in unseen)
        {
            _ = _current.TryRemove(id, out _);
            _ = _leased.TryRemove(id, out _);
        }
        Volatile.Write(ref _catalogCount, count);
        Volatile.Write(ref _heldCount, count - missing);
        Volatile.Write(ref _oldestMissing, oldest);
        return new RetainedCertification(count, Convert.ToHexStringLower(hash.GetHashAndReset()), missing, oldest);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        do
        {
            try
            {
                using var bulk = StorageWork.Bulk();
                if (options.PeersFile is not null && owners.Find(options.Region) is { } owner)
                {
                    var nodes = await new PeersFile(options).ListAsync(stoppingToken).ConfigureAwait(false);
                    var source = nodes.FirstOrDefault(node => node.Region == options.Region && node.Name != options.MachineId
                        && (owner.Node == options.MachineId || node.Name == owner.Node));
                    if (source is not null)
                    {
                        var address = source.InternalListener(options.InternalPort);
                        if (owner.Node != options.MachineId)
                        {
                            await SyncRecordsAsync(address, owner, stoppingToken).ConfigureAwait(false);
                        }
                        await ReconcileAsync(address, source.Name, stoppingToken).ConfigureAwait(false);
                    }
                    _ = await CertifyAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Retained copies are still catching up; catalog records and existing copies are preserved");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SyncRecordsAsync(Uri source, RegionalOwner owner, CancellationToken token)
    {
        var memory = services.GetRequiredService<IMemoryBudget>();
        using var reservation = memory.TryReserve(RegionalRetention.PageBytes * 4, tenant: null);
        if (reservation is null)
        {
            return;
        }
        var engine = services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(token).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            if (!memory.TryGrow(reservation, RegionalRetention.PageSize * 160L))
            {
                return;
            }
            var address = new Uri(source, "/v1/rollout/retained" + (after is null ? "" : "?after=" + Uri.EscapeDataString(after)));
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(RegionalRetention.PageBytes * 4, token).ConfigureAwait(false);
            var page = await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.RetainedPage, token).ConfigureAwait(false)
                ?? throw new IOException("retained catalog page is missing");
            if (page.Entries.Count > RegionalRetention.PageSize || owners.Find(options.Region) != owner)
            {
                return;
            }
            using var admission = await services.GetRequiredService<RegionalHandoff>().EnterRetainedCatalogAsync(owner, token).ConfigureAwait(false);
            if (admission is null || owners.Find(options.Region) != owner || owners.Writer)
            {
                return;
            }
            await using var transaction = await store.BeginDurableTransactionAsync(token).ConfigureAwait(false);
            foreach (var entry in page.Entries)
            {
                var record = entry.Record;
                if (seen.Count >= RegionalRetention.MaxDatabases || !seen.Add(record.Database.Id)
                    || string.CompareOrdinal(after, record.Database.Id) >= 0 || record.Database.Home(options.Region) != options.Region
                    || record.Storage.DatabaseId != record.Database.Id || record.Storage.ProjectId != record.Database.ProjectId
                    || entry.Revision != record.Revision || !DatabaseRevision.TryParse(entry.Revision, out _))
                {
                    throw new InvalidDataException("invalid retained catalog entry");
                }
                after = record.Database.Id;
                if (await DatabasePurge.IsPermanentlyPurgedAsync(transaction, record.Database.Id, token).ConfigureAwait(false))
                {
                    await DeletedDatabases.RemoveAsync(transaction, record, token).ConfigureAwait(false);
                    continue;
                }
                var json = EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(record, ManagementJsonContext.Default.DeletedDatabaseRecord), record.Database.Id, nested: true);
                await transaction.PutAsync(DeletedDatabases.Kind, record.Database.Id, json, token).ConfigureAwait(false);
                await transaction.PutAsync(DeletedDatabases.Project(record.Database.ProjectId), record.Database.Id, json, token).ConfigureAwait(false);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            if (page.Next is null)
            {
                break;
            }
            if (page.Entries.Count == 0 || page.Next != after)
            {
                throw new InvalidDataException("invalid retained catalog cursor");
            }
        }
        while (true);
        after = null;
        do
        {
            var page = await RegionalRetention.RecordsAsync(engine, after, token).ConfigureAwait(false);
            using var admission = await services.GetRequiredService<RegionalHandoff>().EnterRetainedCatalogAsync(owner, token).ConfigureAwait(false);
            if (admission is null || owners.Find(options.Region) != owner || owners.Writer)
            {
                return;
            }
            await using var transaction = await store.BeginDurableTransactionAsync(token).ConfigureAwait(false);
            foreach (var stored in page.Records.Where(record => !seen.Contains(record.Id)))
            {
                await DeletedDatabases.RemoveAsync(transaction, RegionalRetention.Read(stored), token).ConfigureAwait(false);
                _ = _current.TryRemove(stored.Id, out _);
                _ = _leased.TryRemove(stored.Id, out _);
                services.GetRequiredService<RegionReplication>().Resume(RegionalRetention.Read(stored).Storage);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            after = page.HasMore ? page.Records[^1].Id : null;
        }
        while (after is not null);
    }

    private async Task ReconcileAsync(Uri source, string sourceNode, CancellationToken token)
    {
        var engine = services.GetRequiredService<IStorageEngine>();
        var revisions = services.GetRequiredService<IDatabaseRevisions>();
        string? after = null;
        do
        {
            var page = await RegionalRetention.RecordsAsync(engine, after, token).ConfigureAwait(false);
            await Parallel.ForEachAsync(page.Records, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (stored, cancellationToken) =>
            {
                try
                {
                    var record = RegionalRetention.Read(stored);
                    if (!DatabaseRevision.TryParse(record.Revision, out var expected))
                    {
                        throw new InvalidDataException("retained database has no certified position");
                    }
                    if (_current.GetValueOrDefault(stored.Id) == record.Revision && engine.HasDatabaseFiles(record.Storage, cancellationToken)
                        && (!owners.Writer || _leased.GetValueOrDefault(stored.Id) == owners.Find(options.Region)?.Epoch))
                    {
                        return;
                    }
                    using var admission = await services.GetRequiredService<RegionalIngress>().EnterAsync(parkWhileHeld: false, cancellationToken).ConfigureAwait(false);
                    if (admission is null)
                    {
                        return;
                    }
                    using var copy = await services.GetRequiredService<RetentionAuthority>().LockCopyAsync(stored.Id, cancellationToken).ConfigureAwait(false);
                    var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
                    await using (var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false))
                    {
                        if (await SystemCatalog.GetAsync(transaction, DeletedDatabases.Kind, stored.Id,
                                ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false) is not { } retained
                            || retained.DeletedAt != record.DeletedAt || retained.Revision != record.Revision
                            || !DatabasePurge.SameDatabase(retained.Storage, record.Storage) || await transaction.GetAsync(DatabasePurge.Kind, stored.Id, cancellationToken).ConfigureAwait(false) is not null)
                        {
                            return;
                        }
                    }
                    _ = _current.TryRemove(stored.Id, out _);
                    var actual = engine.HasDatabaseFiles(record.Storage, cancellationToken)
                        ? await revisions.CurrentRevisionAsync(record.Storage, cancellationToken).ConfigureAwait(false) : null;
                    if (actual is { } current && current.Incarnation == expected.Incarnation && DatabaseRevision.Compare(current, expected) >= 0
                        || await ImportRetainedAsync(new RetainedEntry(record, record.Revision), source, engine, cancellationToken).ConfigureAwait(false))
                    {
                        if (!Remember(stored.Id, record.Revision))
                        {
                            return;
                        }
                        if (owners.Writer)
                        {
                            var epoch = owners.Find(options.Region)?.Epoch ?? throw new InvalidOperationException("regional owner is missing");
                            if (await services.GetRequiredService<HomeLeases>().TakeRetainedRegionalAsync(record, sourceNode, cancellationToken).ConfigureAwait(false))
                            {
                                _leased[stored.Id] = epoch;
                            }
                        }
                    }
                    else
                    {
                        _ = _current.TryRemove(stored.Id, out _);
                    }
                }
                catch (Exception error) when (error is HttpRequestException or IOException or StorageBusyException)
                {
                    _ = _current.TryRemove(stored.Id, out _);
                    logger.ZLogWarning(error, $"A retained copy is unavailable; its catalog record is preserved and other copies continue catching up");
                }
            }).ConfigureAwait(false);
            after = page.HasMore ? page.Records[^1].Id : null;
        }
        while (after is not null);
    }

    private bool Remember(string id, string revision)
    {
        lock (_cacheGate)
        {
            if (_current.Count >= RegionalRetention.MaxDatabases && !_current.ContainsKey(id))
            {
                return false;
            }
            _current[id] = revision;
            return true;
        }
    }

    private async Task<bool> ImportRetainedAsync(RetainedEntry entry, Uri source, IStorageEngine engine, CancellationToken token)
    {
        var database = entry.Record.Storage;
        if (!DatabaseRevision.TryParse(entry.Revision, out var expected))
        {
            return false;
        }
        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        var replication = services.GetRequiredService<RegionReplication>();
        await replication.SuspendAsync(database).ConfigureAwait(false);
        await services.GetRequiredService<ITransactionCrypto>().PrepareAsync(database, token).ConfigureAwait(false);
        await replicas.OpenAsync(database, new Uri(source, ReplicaRoutes.DatabasePath(database)), _http, token).ConfigureAwait(false);
        _ = await replicas.PullAsync(database, fromHome: true, token).ConfigureAwait(false);
        if (replicas.HeldRevision(database) is not { } actual || actual.Incarnation != expected.Incarnation
            || DatabaseRevision.Compare(actual, expected) < 0)
        {
            return false;
        }
        var detached = await replicas.DetachAsync(database, token).ConfigureAwait(false);
        if (detached is null)
        {
            return false;
        }
        _ = await engine.DeleteDatabaseAsync(database, token).ConfigureAwait(false);
        await services.GetRequiredService<IWalBackup>().ImportDatabaseAsync(database, detached, token).ConfigureAwait(false);
        return true;
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
