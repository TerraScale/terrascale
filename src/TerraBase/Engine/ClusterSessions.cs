using System.Collections.Frozen;
using System.IO.Hashing;
using TerraBase.Cluster;
using TerraBase.Continuity;
using TerraBase.Features.Catalog;
using TerraBase.Features.InternalApi;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;
using ZLogger;

namespace TerraBase.Engine;

internal sealed class ClusterSessions(IServiceProvider services, ISqlStorageEngine engine, DatabaseKeyring keyring) : IContinuityDatabaseCatalog
{
    private const int CatalogPage = 512;
    private static readonly TimeSpan PeerRewindTimeout = TimeSpan.FromSeconds(15);

    private static readonly FrozenDictionary<string, string> ArchiveHomeRegions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sa-saopaulo-1"] = "gru",
        ["eu-frankfurt-1"] = "fra",
        ["us-sanjose-1"] = "sjc",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private TenantDatabases Databases => services.GetRequiredService<TenantDatabases>();

    private SystemDatabase System => services.GetRequiredService<SystemDatabase>();

    private CatalogStore Catalog => services.GetRequiredService<CatalogStore>();

    private ManagementService Management => services.GetRequiredService<ManagementService>();

    private ClusterOptions Cluster => services.GetRequiredService<ClusterOptions>();

    private IClusterCoordinator Coordinator => services.GetRequiredService<IClusterCoordinator>();

    private ContinuityOptions Continuity => services.GetRequiredService<ContinuityOptions>();

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

    public async ValueTask<ArchiveCatchUpOutcome> CatchUpFromArchiveAsync(ISqlDatabaseSession session, CausalBookmark target, CancellationToken cancellationToken)
    {
        var before = session.CurrentBookmark;
        var localOrigin = Cluster.StorageOrigin;
        var remoteTarget = CausalBookmark.Create(target.Entries.Where(entry => !string.Equals(entry.Key, localOrigin, StringComparison.Ordinal)));
        var continuity = services.GetRequiredService<BackupContinuityService>();
        var recovered = await continuity.CatchUpAsync(session, remoteTarget, ReplayArchiveSegmentAsync, cancellationToken);
        return recovered switch
        {
            CausalBookmark after => new ArchiveCatchUpProgress((int)Math.Min(after.Entries.Sum(entry => Math.Clamp(entry.Value - before.Get(entry.Key), 0, int.MaxValue)), int.MaxValue)),
            ContinuityUnavailable unavailable => new ClusterUnavailable(unavailable.Message, unavailable.Code),
        };
    }

    private async ValueTask<ContinuityUnavailable?> ReplayArchiveSegmentAsync(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> entries, CancellationToken cancellationToken)
    {
        var replicator = services.GetRequiredService<DeltaReplicator>();
        var applied = await replicator.ApplyToSessionAsync(session, entries, cancellationToken);
        return applied switch
        {
            RemoteApplyOutcome => null,
            DatabaseNotFound missing => new ContinuityUnavailable(missing.Message, "database_not_found"),
            SchemaMismatch => new ContinuityUnavailable("schema changed during archive catch-up", "serialization_conflict"),
            StorageCorrupted corrupted => new ContinuityUnavailable(corrupted.Message, "storage_corrupted"),
            StorageBusy busy => new ContinuityUnavailable(busy.Message, "storage_busy"),
            ClusterUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
        };
    }

    public async ValueTask<bool> IsPreferredArchiverAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var cluster = Cluster;
        if (cluster.Peers.Count == 0)
        {
            return true;
        }

        ArchiverCandidate[] healthy =
        [
            new(cluster.NodeId, cluster.Region),
            .. Coordinator.Peers
                .Where(static health => health.Status == PeerStatus.Healthy)
                .Select(static health => new ArchiverCandidate(health.Peer.NodeId, health.Peer.Region)),
        ];
        ArchiverCandidate[] eligible = healthy;
        if (databaseId != SystemDatabase.Id)
        {
            var found = await Catalog.GetDatabaseAsync(databaseId, cancellationToken);
            var placed = found switch
            {
                DatabaseRecord database => database,
                ResourceNotFound or BackendUnavailable => null,
            };
            if (placed is not null)
            {
                eligible = [.. healthy.Where(candidate => placed.PlacedIn(candidate.Region))];
                if (eligible.Length == 0)
                {
                    eligible = healthy;
                }
            }
        }

        var home = ArchiveHomeRegion(cluster);
        ArchiverCandidate[] preferred = home is null ? [] : [.. eligible.Where(candidate => string.Equals(candidate.Region, home, StringComparison.Ordinal))];
        ArchiverCandidate[] candidates = [.. (preferred.Length > 0 ? preferred : eligible).OrderBy(static candidate => candidate.NodeId, StringComparer.Ordinal)];
        var chosen = candidates[(int)(ArchiverHash(databaseId) % (ulong)candidates.Length)];
        return string.Equals(chosen.NodeId, cluster.NodeId, StringComparison.Ordinal);
    }

    private string? ArchiveHomeRegion(ClusterOptions cluster)
    {
        if (Continuity.Archive is not { } archive)
        {
            return null;
        }

        if (cluster.Regions.Contains(archive.Region, StringComparer.Ordinal))
        {
            return archive.Region;
        }

        return ArchiveHomeRegions.TryGetValue(archive.Region, out var mapped) ? mapped : null;
    }

    private static ulong ArchiverHash(Ulid databaseId)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = databaseId.TryWriteBytes(bytes);
        return XxHash64.HashToUInt64(bytes);
    }

    public async ValueTask<IReadOnlyList<Ulid>> ListActiveDatabasesAsync(CancellationToken cancellationToken)
    {
        List<Ulid> active = [SystemDatabase.Id];
        string? after = null;
        while (true)
        {
            var listed = await Catalog.AllDatabasesAsync(after, CatalogPage, cancellationToken);
            var page = listed switch
            {
                IReadOnlyList<DatabaseRecord> records => records,
                BackendUnavailable => null,
            };
            if (page is null)
            {
                return [];
            }

            if (page.Count == 0)
            {
                return active;
            }

            active.AddRange(page.Select(static record => record.Id));
            after = page[^1].Id.ToString();
        }
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

    public async ValueTask<CausalBookmark> ConfirmReplicaWatermarksAsync(Ulid databaseId, CausalBookmark local, CancellationToken cancellationToken)
    {
        var cluster = Cluster;
        if (cluster.Peers.Count == 0)
        {
            return local;
        }

        if (databaseId == SystemDatabase.Id)
        {
            return await ReplicaWatermarks.ConfirmAsync(Quic, cluster, cluster.Peers, databaseId, local, cancellationToken);
        }

        var found = await Catalog.GetDatabaseAsync(databaseId, cancellationToken);
        return found switch
        {
            DatabaseRecord record => await ReplicaWatermarks.ConfirmAsync(
                Quic, cluster, [.. cluster.Peers.Where(peer => record.PlacedIn(peer.Region))], databaseId, local, cancellationToken),
            ResourceNotFound or BackendUnavailable => CausalBookmark.Empty,
        };
    }

    public async ValueTask DatabaseReplacedAsync(Ulid databaseId, CausalBookmark? rewoundTo, CancellationToken cancellationToken)
    {
        await Databases.EvictAsync(databaseId);
        services.GetRequiredService<ArchiveManifests>().Invalidate(databaseId);
        services.GetRequiredService<ChangefeedHub>().Activity.Invalidate(databaseId);
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
        if (Databases.TryGetTracked(databaseId, placement.KeyId, out _))
        {
            return await LeaseAsync(databaseId, placement.KeyId, cancellationToken);
        }

        var leased = await LeaseAsync(databaseId, placement.KeyId, cancellationToken);
        return leased switch
        {
            ISqlDatabaseSession or ClusterUnavailable => leased,
            DatabaseNotFound => await CreatePlacedAsync(databaseId, placement, cancellationToken),
        };
    }

    private async ValueTask<DatabaseResolveOutcome> CreatePlacedAsync(Ulid databaseId, DatabasePlacement placement, CancellationToken cancellationToken)
    {
        var ready = await Databases.OpenOrCreateForReplicationAsync(databaseId, placement.KeyId, placement.Strict, cancellationToken);
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

internal readonly record struct ArchiverCandidate(string NodeId, string Region);

internal readonly union DatabasePlacementOutcome(DatabasePlacement, ResourceNotFound, BackendUnavailable);
