using TerraBase.Cluster;
using TerraBase.Continuity;
using TerraBase.Features.Catalog;
using TerraBase.Features.InternalApi;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;
using ZLogger;

namespace TerraBase.Engine;

internal sealed class ClusterSessions(IServiceProvider services, ISqlStorageEngine engine, DatabaseKeyring keyring) : IContinuityDatabaseCatalog
{
    private const int CatalogPage = 512;
    private static readonly TimeSpan PeerRewindTimeout = TimeSpan.FromSeconds(15);

    private TenantDatabases Databases => services.GetRequiredService<TenantDatabases>();

    private SystemDatabase System => services.GetRequiredService<SystemDatabase>();

    private CatalogStore Catalog => services.GetRequiredService<CatalogStore>();

    private ManagementService Management => services.GetRequiredService<ManagementService>();

    private ClusterOptions Cluster => services.GetRequiredService<ClusterOptions>();

    private QuicRpcClient Quic => services.GetRequiredService<QuicRpcClient>();

    private ILogger<ClusterSessions> Logger => services.GetRequiredService<ILogger<ClusterSessions>>();

    public async ValueTask<DatabaseResolveOutcome> ResolveAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var placed = await PlacementAsync(databaseId, cancellationToken);
        return placed switch
        {
            DatabasePlacement placement => await OpenPlacedAsync(databaseId, placement, cancellationToken),
            ResourceNotFound missing => new DatabaseNotFound(databaseId, missing.Message),
            BackendUnavailable unavailable => new ClusterUnavailable(unavailable.Message, unavailable.Code),
        };
    }

    public async ValueTask<IReadOnlyList<Ulid>> ListActiveDatabasesAsync(CancellationToken cancellationToken)
    {
        List<Ulid> active = [SystemDatabase.Id];
        string? after = null;
        while (await Catalog.AllDatabasesAsync(after, CatalogPage, cancellationToken) switch
        {
            IReadOnlyList<DatabaseRecord> records => records,
            BackendUnavailable => null,
        } is { Count: > 0 } page)
        {
            active.AddRange(page.Select(static record => record.Id));
            after = page[^1].Id.ToString();
        }

        return active;
    }

    public async ValueTask<ContinuityRawKeyOutcome> ResolveRawKeyAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var placed = await PlacementAsync(databaseId, cancellationToken);
        return placed switch
        {
            DatabasePlacement placement => keyring.Derive(databaseId, placement.KeyId) switch
            {
                DatabaseKey key => new ContinuityRawKey(key.RawKey),
                MasterKeyMissing missing => new ContinuityDatabaseKeyMissing(databaseId, string.Concat("master key '", missing.KeyId, "' is not configured"), "unknown_master_key"),
            },
            ResourceNotFound missing => new ContinuityDatabaseKeyMissing(databaseId, missing.Message, missing.Code),
            BackendUnavailable unavailable => new ContinuityDatabaseKeyMissing(databaseId, unavailable.Message, unavailable.Code),
        };
    }

    public async ValueTask DatabaseReplacedAsync(Ulid databaseId, CausalBookmark? rewoundTo, CancellationToken cancellationToken)
    {
        await Databases.EvictAsync(databaseId);
        if (rewoundTo is not { } bookmark || Cluster.Peers.Count == 0)
        {
            return;
        }

        var request = new RewindDatabaseRequest(databaseId, bookmark.Encode());
        await Task.WhenAll(Cluster.Peers.Select(peer => RewindPeerAsync(peer.NodeId, request, cancellationToken)));
    }

    private async Task RewindPeerAsync(string nodeId, RewindDatabaseRequest request, CancellationToken cancellationToken)
    {
        var outcome = await Quic.CallAsync<RewindDatabaseRequest, RewindDatabaseReply>(nodeId, QuicMessageType.RewindDatabase, request, PeerRewindTimeout, cancellationToken);
        if (outcome switch
        {
            RewindDatabaseReply => null,
            QuicUnavailable unavailable => unavailable.Message,
            QuicTimeout timeout => timeout.Message,
            QuicRemoteError error => error.Message,
        } is { } failure)
        {
            Logger.ZLogWarning($"Peer {nodeId} did not rewind database {request.DatabaseId}: {failure}");
        }
    }

    public async ValueTask<DiscardCopyTargetOutcome> DiscardCopyTargetAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var discarded = await Management.DiscardCopyAsync(databaseId, cancellationToken);
        return discarded switch
        {
            Completed => new CopyTargetDiscarded(),
            BackendUnavailable unavailable => new CopyTargetDiscardFailed(unavailable.Message, unavailable.Code),
        };
    }

    private async ValueTask<DatabasePlacementOutcome> PlacementAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (databaseId == SystemDatabase.Id)
        {
            return new DatabasePlacement(System.KeyId, Strict: false);
        }

        var found = await Catalog.GetDatabaseAsync(databaseId, cancellationToken);
        return found switch
        {
            DatabaseRecord record => new DatabasePlacement(record.KeyId, record.Strict),
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<DatabaseResolveOutcome> OpenPlacedAsync(Ulid databaseId, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        var ready = await Databases.OpenOrCreateAsync(databaseId, placement.KeyId, placement.Strict, cancellationToken);
        return ready switch
        {
            ISqlDatabaseSession => await LeaseAsync(databaseId, placement.KeyId, cancellationToken),
            BackendUnavailable unavailable => new ClusterUnavailable(unavailable.Message, unavailable.Code),
        };
    }

    private async ValueTask<DatabaseResolveOutcome> LeaseAsync(Ulid databaseId, string keyId, CancellationToken cancellationToken) =>
        keyring.Derive(databaseId, keyId) switch
        {
            DatabaseKey key => await OpenLeasedAsync(databaseId, key, cancellationToken),
            MasterKeyMissing => new ClusterUnavailable("the database's master key is not configured on this node", "master_key_missing"),
        };

    private async ValueTask<DatabaseResolveOutcome> OpenLeasedAsync(Ulid databaseId, DatabaseKey key, CancellationToken cancellationToken)
    {
        var opened = await engine.OpenAsync(new DatabaseOpenRequest(databaseId, key.RawKey), cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession session => Leased(session),
            DatabaseNotFound missing => missing,
            InvalidDatabaseRequest invalid => new ClusterUnavailable(invalid.Message, "database_key_mismatch"),
            StorageCorrupted corrupted => new ClusterUnavailable(corrupted.Message, "storage_corrupted"),
            StorageBusy busy => new ClusterUnavailable(busy.Message, "storage_busy"),
        };
    }

    private static DatabaseResolveOutcome Leased(ISqlDatabaseSession session) => new(session);
}

internal sealed record DatabasePlacement(string KeyId, bool Strict);

internal readonly union DatabasePlacementOutcome(DatabasePlacement, ResourceNotFound, BackendUnavailable);
