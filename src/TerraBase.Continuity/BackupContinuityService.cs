using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace TerraBase.Continuity;

internal enum OperationKind
{
    Restore,
    Branch,
    Rewind,
}

internal sealed record OperationWork(Ulid OperationId, OperationKind Kind, Ulid SourceDatabaseId, Ulid TargetDatabaseId, RestoreTarget Target);

internal sealed record FinishedOperation(RestoreOperation Operation, string Kind, string? RecordFailure);

internal sealed record ArchiveProof(CausalBookmark Bookmark, DateTimeOffset CheckedAtUtc);

internal sealed record ShredFound;

internal sealed record ShredAbsent;

internal readonly union ShredPresence(ShredFound, ShredAbsent, ContinuityUnavailable);

internal sealed record SourceReady;

internal readonly union SourceAvailability(SourceReady, ContinuityNotFound, ContinuityUnavailable);

internal sealed record ArchiveTruncated;

internal sealed record ScheduledArchiveStatus(BackupStatus Status, bool DeferCache);

internal readonly union ScheduledArchiveOutcome(ScheduledArchiveStatus, ContinuityNotFound, ContinuityUnavailable);

internal readonly union ArchiveTruncateOutcome(ArchiveTruncated, ContinuityUnavailable);

public delegate ValueTask<ContinuityUnavailable?> ReplayArchiveSegment(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> entries, CancellationToken cancellationToken);

public readonly union ContinuityCatchUpOutcome(CausalBookmark, ContinuityUnavailable);

public readonly union ContinuityRewindOutcome(CausalBookmark, ContinuityUnavailable);

public sealed class BackupContinuityService(
    ContinuitySessions sessions,
    ArchiveManifests manifests,
    DatabaseRestorer restorer,
    ContinuityOptions options,
    ContinuityMetrics metrics,
    TimeProvider timeProvider) : IBackupContinuity
{
    private static readonly ContinuityUnavailable Disabled = new("continuous backup is disabled on this node", "backup_disabled");
    private static readonly TimeSpan PeerRewindRecheckWindow = S3ArchiveStore.RequestTimeout * 3;

    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, RestoreOperation> _operations = [];
    private readonly Queue<Ulid> _finished = new();
    private readonly HashSet<Ulid> _claimed = [];
    private readonly ConcurrentDictionary<Ulid, PendingPeerRewind> _pendingPeerRewinds = new();
    private readonly Channel<OperationWork> _queue = Channel.CreateBounded<OperationWork>(new BoundedChannelOptions(options.OperationQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    public bool IsBackupEnabled => manifests.IsEnabled;

    public async ValueTask<BackupStatusOutcome> GetStatusAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (!manifests.IsEnabled)
        {
            return Disabled;
        }

        var loaded = await manifests.ReadCurrentAsync(databaseId, cancellationToken);
        return loaded switch
        {
            ArchiveManifest manifest => await StatusAsync(manifest, databaseId, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    public ValueTask<BackupStatusOutcome> ArchiveNowAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        ArchiveAsync(databaseId, forceSnapshot: true, cancellationToken);

    public async ValueTask<BackupStatusOutcome> ArchiveAsync(Ulid databaseId, bool forceSnapshot, CancellationToken cancellationToken)
    {
        if (!manifests.IsEnabled)
        {
            return Disabled;
        }

        using var lease = await manifests.AcquireAsync(databaseId, cancellationToken);
        return await ArchiveLockedAsync(databaseId, forceSnapshot, cancellationToken);
    }

    internal async ValueTask<ScheduledArchiveOutcome> ArchiveScheduledAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (!manifests.IsEnabled)
        {
            return Disabled;
        }

        using var lease = await manifests.AcquireAsync(databaseId, cancellationToken);
        return await ArchiveScheduledLockedAsync(databaseId, forceSnapshot: false, scheduled: true, cancellationToken);
    }

    internal void PruneInactive(HashSet<Ulid> active)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (databaseId, pending) in _pendingPeerRewinds)
        {
            if (!active.Contains(databaseId) || now >= pending.ExpiresAtUtc)
            {
                _ = _pendingPeerRewinds.TryRemove(new KeyValuePair<Ulid, PendingPeerRewind>(databaseId, pending));
            }
        }
    }

    public ValueTask<RestoreStartOutcome> RestoreAsync(PointInTimeRestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EnqueueCopyAsync(OperationKind.Restore, request.SourceDatabaseId, request.TargetDatabaseId, request.Target, cancellationToken);
    }

    public ValueTask<RestoreStartOutcome> BranchAsync(BranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EnqueueCopyAsync(OperationKind.Branch, request.SourceDatabaseId, request.BranchDatabaseId, request.Target, cancellationToken);
    }

    public async ValueTask<RestoreStartOutcome> RewindAsync(Ulid databaseId, RestoreTarget target, CancellationToken cancellationToken)
    {
        if (ValidateTarget(target) is { } invalid)
        {
            return invalid;
        }

        if (!restorer.Layout.Exists(databaseId))
        {
            return new ContinuityNotFound("database is not stored on this node", "database_not_local");
        }

        var resolved = await sessions.ResolveKeyAsync(databaseId, cancellationToken);
        return resolved switch
        {
            ContinuityRawKey => Enqueue(OperationKind.Rewind, databaseId, databaseId, target),
            ContinuityDatabaseKeyMissing missing => new ContinuityNotFound(missing.Message, missing.Code),
        };
    }

    public ValueTask<RestoreOperationOutcome> GetOperationAsync(Ulid operationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RestoreOperationOutcome outcome = _operations.TryGetValue(operationId, out var operation)
                ? operation
                : new ContinuityNotFound("continuity operation not found", "operation_not_found");
            return ValueTask.FromResult(outcome);
        }
    }

    public async ValueTask<CryptoShredBackupOutcome> CryptoShredAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        using var lease = await manifests.AcquireAsync(databaseId, cancellationToken);
        _ = _pendingPeerRewinds.TryRemove(databaseId, out _);
        var shredded = await sessions.Engine.CryptoShredAsync(databaseId, cancellationToken);
        var local = shredded switch
        {
            DatabaseShredded => (ShredPresence)new ShredFound(),
            DatabaseNotFound => new ShredAbsent(),
            StorageBusy busy => new ContinuityUnavailable(busy.Message, "storage_busy"),
        };
        var remote = manifests.IsEnabled
            ? await RemotePresenceAsync(databaseId, cancellationToken)
            : new ShredAbsent();
        return local switch
        {
            ContinuityUnavailable unavailable => unavailable,
            ShredFound => remote switch
            {
                ContinuityUnavailable unavailable => unavailable,
                ShredFound => new ArchiveShredded(),
                ShredAbsent => new ArchiveShredded(),
            },
            ShredAbsent => remote switch
            {
                ContinuityUnavailable unavailable => unavailable,
                ShredFound => new ArchiveShredded(),
                ShredAbsent => new ContinuityNotFound("database has no local files or archived objects"),
            },
        };
    }

    internal IAsyncEnumerable<OperationWork> PendingOperationsAsync(CancellationToken cancellationToken) => _queue.Reader.ReadAllAsync(cancellationToken);

    internal async ValueTask<FinishedOperation> ExecuteAsync(OperationWork work, CancellationToken cancellationToken)
    {
        Transition(work.OperationId, RestoreOperationState.Running, null);
        var performed = work.Kind switch
        {
            OperationKind.Restore or OperationKind.Branch => await restorer.CopyAsync(work.OperationId, work.SourceDatabaseId, work.TargetDatabaseId, work.Target, cancellationToken),
            OperationKind.Rewind => await RewindLockedAsync(work, cancellationToken),
            _ => throw new UnreachableException(),
        };
        return performed switch
        {
            CausalBookmark => await FinishAsync(work, null, record: true, cancellationToken),
            OperationFailed failed => await FailAsync(work, failed.Message, cancellationToken),
        };
    }

    internal async ValueTask<FinishedOperation> FaultAsync(OperationWork work, string message) =>
        await FailAsync(work, message, CancellationToken.None);

    private async ValueTask<FinishedOperation> FailAsync(OperationWork work, string message, CancellationToken cancellationToken) =>
        work.Kind switch
        {
            OperationKind.Restore or OperationKind.Branch => await DiscardTargetAsync(work, message, cancellationToken),
            OperationKind.Rewind => await FinishAsync(work, message, record: true, cancellationToken),
            _ => throw new UnreachableException(),
        };

    private async ValueTask<FinishedOperation> DiscardTargetAsync(OperationWork work, string message, CancellationToken cancellationToken)
    {
        var discarded = await sessions.Catalog.DiscardCopyTargetAsync(work.TargetDatabaseId, CancellationToken.None);
        return discarded switch
        {
            CopyTargetDiscarded => await FinishAsync(work, message, record: false, cancellationToken),
            CopyTargetDiscardFailed failed => await FinishAsync(
                work,
                string.Concat(message, "; the target database could not be discarded: ", failed.Message),
                record: true,
                cancellationToken),
        };
    }

    private ValueTask<RestoreStartOutcome> EnqueueCopyAsync(
        OperationKind kind,
        Ulid sourceId,
        Ulid targetId,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        if (sourceId == targetId)
        {
            RestoreStartOutcome sameDatabase = new ContinuityInvalidTarget("source and target databases must differ", "same_database");
            return ValueTask.FromResult(sameDatabase);
        }

        if (ValidateTarget(target) is { } invalid)
        {
            RestoreStartOutcome rejected = invalid;
            return ValueTask.FromResult(rejected);
        }

        if (restorer.Layout.DirectoryExists(targetId))
        {
            RestoreStartOutcome exists = new ContinuityConflict("target database already exists on this node", "database_exists");
            return ValueTask.FromResult(exists);
        }

        return EnqueueResolvedCopyAsync(kind, sourceId, targetId, target, cancellationToken);
    }

    private async ValueTask<RestoreStartOutcome> EnqueueResolvedCopyAsync(
        OperationKind kind,
        Ulid sourceId,
        Ulid targetId,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        var sourceKey = await sessions.ResolveKeyAsync(sourceId, cancellationToken);
        var targetKey = await sessions.ResolveKeyAsync(targetId, cancellationToken);
        return sourceKey switch
        {
            ContinuityDatabaseKeyMissing missing => new ContinuityNotFound(missing.Message, missing.Code),
            ContinuityRawKey => await MatchTargetAsync(targetKey),
        };

        async ValueTask<RestoreStartOutcome> MatchTargetAsync(ContinuityRawKeyOutcome resolvedTarget) => resolvedTarget switch
        {
            ContinuityDatabaseKeyMissing missing => new ContinuityNotFound(missing.Message, missing.Code),
            ContinuityRawKey => await MatchSourceAsync(await SourceAvailableAsync(sourceId, cancellationToken)),
        };

        ValueTask<RestoreStartOutcome> MatchSourceAsync(SourceAvailability available)
        {
            RestoreStartOutcome started = available switch
            {
                SourceReady => Enqueue(kind, sourceId, targetId, target),
                ContinuityNotFound missing => missing,
                ContinuityUnavailable unavailable => unavailable,
            };
            return ValueTask.FromResult(started);
        }
    }

    private async ValueTask<SourceAvailability> SourceAvailableAsync(Ulid sourceId, CancellationToken cancellationToken)
    {
        if (restorer.Layout.Exists(sourceId))
        {
            return new SourceReady();
        }

        if (!manifests.IsEnabled)
        {
            return new ContinuityNotFound("source database is not stored on this node and continuous backup is disabled", "database_not_local");
        }

        var loaded = await manifests.LoadAsync(sourceId, cancellationToken);
        return loaded switch
        {
            ArchiveManifest { Snapshots.Count: > 0 } => new SourceReady(),
            ArchiveManifest => new ContinuityNotFound("source database has no local files or archived snapshots", "no_restore_point"),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private RestoreStartOutcome Enqueue(OperationKind kind, Ulid sourceId, Ulid targetId, RestoreTarget target)
    {
        var now = timeProvider.GetUtcNow();
        var operation = new RestoreOperation(Ulid.NewUlid(now), sourceId, targetId, RestoreOperationState.Pending, now, now, null);
        lock (_gate)
        {
            if (!_claimed.Add(targetId))
            {
                return new ContinuityConflict("another continuity operation is already running for this database", "operation_in_progress");
            }

            if (!_queue.Writer.TryWrite(new OperationWork(operation.OperationId, kind, sourceId, targetId, target)))
            {
                _ = _claimed.Remove(targetId);
                return new ContinuityUnavailable("the continuity operation queue is full", "operation_queue_full");
            }

            _operations[operation.OperationId] = operation;
        }

        return operation;
    }

    private ContinuityInvalidTarget? ValidateTarget(RestoreTarget target) => target switch
    {
        DateTimeOffset at when at > timeProvider.GetUtcNow() => new ContinuityInvalidTarget("the restore point is in the future", "restore_point_in_future"),
        DateTimeOffset => null,
        CausalBookmark => null,
    };

    public async ValueTask<ContinuityCatchUpOutcome> CatchUpAsync(ISqlDatabaseSession session, CausalBookmark target, ReplayArchiveSegment replay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        if (!manifests.IsEnabled)
        {
            return Disabled;
        }

        using var lease = await manifests.AcquireAsync(session.DatabaseId, cancellationToken);
        var loaded = await manifests.ReadCurrentAsync(session.DatabaseId, cancellationToken);
        return loaded switch
        {
            ArchiveManifest manifest => await ReplayArchivedSegmentsAsync(session, target, manifest, replay, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ContinuityCatchUpOutcome> ReplayArchivedSegmentsAsync(
        ISqlDatabaseSession session, CausalBookmark target, ArchiveManifest manifest, ReplayArchiveSegment replay, CancellationToken cancellationToken)
    {
        const int maxSegments = 32;
        var before = session.CurrentBookmark;
        var imported = 0;
        foreach (var group in manifest.Segments.GroupBy(static segment => segment.Origin, StringComparer.Ordinal))
        {
            var through = target.Get(group.Key);
            var cursor = session.CurrentBookmark.Get(group.Key);
            foreach (var segment in group.OrderBy(static segment => segment.FirstSequence))
            {
                if (cursor >= through || segment.FirstSequence > cursor + 1 || imported >= maxSegments)
                {
                    break;
                }

                if (segment.LastSequence <= cursor)
                {
                    continue;
                }

                if (await ReplayArchivedSegmentAsync(session, segment, replay, cancellationToken) is { } failure)
                {
                    return failure;
                }

                imported++;
                cursor = segment.LastSequence;
            }
        }

        var current = session.CurrentBookmark;
        return before.Dominates(current)
            ? new ContinuityUnavailable("the archive has no applicable segments for the commit log gap", "archive_gap")
            : current;
    }

    private async ValueTask<ContinuityUnavailable?> ReplayArchivedSegmentAsync(
        ISqlDatabaseSession session, ArchivedSegment segment, ReplayArchiveSegment replay, CancellationToken cancellationToken)
    {
        var staging = restorer.Layout.StagingDirectory(Ulid.NewUlid(timeProvider.GetUtcNow()));
        var created = ContinuityFiles.CreateDirectory(staging);
        if (created switch
        {
            ContinuityFilesChanged => null,
            ContinuityIoFailure io => new ContinuityUnavailable(io.Message, io.Code),
        } is { } createFailure)
        {
            return createFailure;
        }

        try
        {
            var path = Path.Combine(staging, "segment.tblog");
            var fetched = await manifests.Store.DownloadToFileAsync(segment.Key, path, segment.SizeBytes, cancellationToken);
            return fetched switch
            {
                long bytes when bytes == segment.SizeBytes => await ReplayDownloadedSegmentAsync(session, segment, path, replay, cancellationToken),
                long => new ContinuityUnavailable("archived segment size does not match its manifest", "invalid_segment"),
                ArchiveObjectTooLarge => new ContinuityUnavailable("archived segment exceeds its declared size", "invalid_segment"),
                ArchiveObjectMissing missing => new ContinuityUnavailable(missing.Message, missing.Code),
                ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
                ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
            };
        }
        finally
        {
            restorer.Cleanup(staging);
        }
    }

    private static async ValueTask<ContinuityUnavailable?> ReplayDownloadedSegmentAsync(
        ISqlDatabaseSession session, ArchivedSegment segment, string path, ReplayArchiveSegment replay, CancellationToken cancellationToken)
    {
        var opened = ContinuityFiles.OpenRead(path);
        return opened switch
        {
            Stream content => await ReadAndReplayAsync(content, cancellationToken),
            ContinuityIoFailure failure => new ContinuityUnavailable(failure.Message, failure.Code),
        };

        async ValueTask<ContinuityUnavailable?> ReadAndReplayAsync(Stream content, CancellationToken token)
        {
            await using (content)
            {
                ContinuityUnavailable? replayFailure = null;
                var descriptor = new CommitLogSegment(session.DatabaseId, segment.Origin, segment.FirstSequence, segment.LastSequence, segment.SizeBytes, segment.SealedAtUtc);
                var result = await session.ReadArchivedSegmentAsync(descriptor, content, async (entries, batchToken) =>
                {
                    replayFailure = await replay(session, entries, batchToken);
                    return replayFailure is null;
                }, token);
                return result switch
                {
                    ArchivedSegmentRead => null,
                    ArchivedSegmentReplayStopped => replayFailure,
                    InvalidSegment invalid => new ContinuityUnavailable(invalid.Message, invalid.Code),
                    StorageCorrupted corrupted => new ContinuityUnavailable(corrupted.Message, "storage_corrupted"),
                };
            }
        }
    }

    public async ValueTask<ContinuityRewindOutcome> RewindLocalAsync(Ulid databaseId, CausalBookmark bookmark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        using var lease = await manifests.AcquireAsync(databaseId, cancellationToken);
        var cachedBefore = manifests.TryGetCached(databaseId);
        var operationId = Ulid.NewUlid(timeProvider.GetUtcNow());
        var rewound = await restorer.RewindAsync(operationId, databaseId, new RestoreTarget(bookmark), notifyPeers: false, cancellationToken);
        return rewound switch
        {
            CausalBookmark applied => TrackPeerRewind(databaseId, applied, cachedBefore),
            OperationFailed failed => new ContinuityUnavailable(failed.Message, "rewind_failed"),
        };
    }

    private ContinuityRewindOutcome TrackPeerRewind(Ulid databaseId, CausalBookmark rewoundTo, ArchiveManifest? cachedBefore)
    {
        if (!manifests.IsEnabled)
        {
            return rewoundTo;
        }

        if (!restorer.Layout.Exists(databaseId))
        {
            _ = _pendingPeerRewinds.TryRemove(databaseId, out _);
            return rewoundTo;
        }

        var marks = cachedBefore is not null ? PreRewindMarksBeyond(cachedBefore, rewoundTo) : null;
        _pendingPeerRewinds[databaseId] = new PendingPeerRewind(
            rewoundTo,
            marks is { Count: > 0 } ? marks : null,
            timeProvider.GetUtcNow() + PeerRewindRecheckWindow);
        return rewoundTo;
    }

    private async ValueTask<ContinuityCopyOutcome> RewindLockedAsync(OperationWork work, CancellationToken cancellationToken)
    {
        using var lease = await manifests.AcquireAsync(work.TargetDatabaseId, cancellationToken);
        var rewound = await restorer.RewindAsync(work.OperationId, work.TargetDatabaseId, work.Target, notifyPeers: true, cancellationToken);
        return rewound switch
        {
            CausalBookmark bookmark when manifests.IsEnabled => await ReconcileArchiveAsync(work.TargetDatabaseId, bookmark, cancellationToken),
            CausalBookmark bookmark => bookmark,
            OperationFailed failed => failed,
        };
    }

    private async ValueTask<ContinuityCopyOutcome> ReconcileArchiveAsync(Ulid databaseId, CausalBookmark rewoundTo, CancellationToken cancellationToken)
    {
        var loaded = await manifests.ReadCurrentAsync(databaseId, cancellationToken);
        ArchiveTruncateOutcome truncated = loaded switch
        {
            ArchiveManifest manifest => await TruncateAsync(manifest, rewoundTo, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
        var archived = truncated switch
        {
            ArchiveTruncated => await ArchiveLockedAsync(databaseId, forceSnapshot: true, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
        return archived switch
        {
            BackupStatus => Reconciled(databaseId, rewoundTo),
            ContinuityNotFound notFound => new OperationFailed(string.Concat("database was rewound but could not be archived: ", notFound.Message)),
            ContinuityUnavailable unavailable => new OperationFailed(string.Concat("database was rewound but could not be archived: ", unavailable.Message)),
        };
    }

    private ContinuityCopyOutcome Reconciled(Ulid databaseId, CausalBookmark rewoundTo)
    {
        _ = _pendingPeerRewinds.TryRemove(databaseId, out _);
        return rewoundTo;
    }

    private async ValueTask<ArchiveTruncateOutcome> TruncateAsync(ArchiveManifest manifest, CausalBookmark rewoundTo, CancellationToken cancellationToken)
    {
        var snapshots = ManifestCodec.Snapshots(manifest);
        var keptSnapshots = snapshots.Where(snapshot => rewoundTo.Dominates(snapshot.Bookmark)).Select(static snapshot => snapshot.Archived).ToArray();
        var keptSegments = manifest.Segments.Where(segment => segment.LastSequence <= rewoundTo.Get(segment.Origin)).ToArray();
        var keptKeys = keptSnapshots.Select(static snapshot => snapshot.Key).Concat(keptSegments.Select(static segment => segment.Key)).ToHashSet(StringComparer.Ordinal);
        var dropped = manifest.Snapshots.Select(static snapshot => snapshot.Key)
            .Concat(manifest.Segments.Select(static segment => segment.Key))
            .Where(key => !keptKeys.Contains(key))
            .ToArray();
        if (dropped.Length == 0)
        {
            return new ArchiveTruncated();
        }

        if (!Ulid.TryParse(manifest.DatabaseId, out var databaseId))
        {
            return new ContinuityUnavailable("manifest database id is invalid", "archive_manifest_corrupted");
        }

        var saved = await manifests.UpdateAsync(
            databaseId,
            current =>
            {
                var currentSnapshots = ManifestCodec.Snapshots(current)
                    .Where(snapshot => rewoundTo.Dominates(snapshot.Bookmark))
                    .Select(static snapshot => snapshot.Archived)
                    .ToArray();
                var currentSegments = current.Segments.Where(segment => segment.LastSequence <= rewoundTo.Get(segment.Origin)).ToArray();
                return current with { Snapshots = currentSnapshots, Segments = currentSegments };
            },
            cancellationToken);
        return saved switch
        {
            ArchiveManifest => await DeletedAsync(dropped, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ArchiveTruncateOutcome> DeletedAsync(IReadOnlyList<string> dropped, CancellationToken cancellationToken)
    {
        var deleted = await manifests.DeleteObjectsAsync(dropped, cancellationToken);
        return deleted switch
        {
            ArchiveObjectsDeleted => new ArchiveTruncated(),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ShredPresence> RemotePresenceAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var purged = await manifests.PurgeAsync(databaseId, cancellationToken);
        return purged switch
        {
            ArchivePurged { RemovedObjects: true } => new ShredFound(),
            ArchivePurged => new ShredAbsent(),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<BackupStatusOutcome> ArchiveLockedAsync(Ulid databaseId, bool forceSnapshot, CancellationToken cancellationToken)
    {
        var archived = await ArchiveScheduledLockedAsync(databaseId, forceSnapshot, scheduled: false, cancellationToken);
        return archived switch
        {
            ScheduledArchiveStatus scheduled => scheduled.Status,
            ContinuityNotFound missing => missing,
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ScheduledArchiveOutcome> ArchiveScheduledLockedAsync(
        Ulid databaseId,
        bool forceSnapshot,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        var opened = await sessions.OpenAsync(databaseId, cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession session => await ArchiveSessionAsync(session, forceSnapshot, scheduled, cancellationToken),
            ContinuitySessionMissing missing when !forceSnapshot => await RetainRemoteArchiveScheduledAsync(missing, cancellationToken),
            ContinuitySessionMissing missing => new ContinuityNotFound(missing.Message, missing.Code),
            ContinuitySessionUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
        };
    }

    private async ValueTask<ScheduledArchiveOutcome> RetainRemoteArchiveScheduledAsync(ContinuitySessionMissing missing, CancellationToken cancellationToken)
    {
        var retained = await RetainRemoteArchiveAsync(missing, cancellationToken);
        return retained switch
        {
            BackupStatus status => new ScheduledArchiveStatus(status, DeferCache: false),
            ContinuityNotFound notFound => notFound,
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<BackupStatusOutcome> RetainRemoteArchiveAsync(ContinuitySessionMissing missing, CancellationToken cancellationToken)
    {
        var loaded = await manifests.LoadAsync(missing.DatabaseId, cancellationToken);
        return loaded switch
        {
            ArchiveManifest { IsEmpty: true } => new ContinuityNotFound(missing.Message, missing.Code),
            ArchiveManifest => await RetainRemoteArchiveAsync(missing.DatabaseId, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<BackupStatusOutcome> RetainRemoteArchiveAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var cutoff = await sessions.ReadRetentionCutoffAsync(databaseId, cancellationToken);
        var saved = await manifests.PublishAsync(databaseId, [], null, cutoff, cancellationToken);
        return saved switch
        {
            ArchiveManifest stored => Status(stored, 0, null),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ScheduledArchiveOutcome> ArchiveSessionAsync(
        ISqlDatabaseSession session,
        bool forceSnapshot,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        await using (session)
        {
            var loaded = await manifests.ReadCurrentAsync(session.DatabaseId, cancellationToken);
            return loaded switch
            {
                ArchiveManifest manifest => await ArchivedAsync(manifest, session, forceSnapshot, scheduled, cancellationToken),
                ContinuityUnavailable unavailable => unavailable,
            };
        }
    }

    private async ValueTask<ScheduledArchiveOutcome> ArchivedAsync(
        ArchiveManifest manifest,
        ISqlDatabaseSession session,
        bool forceSnapshot,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        var archiver = new SessionArchiver(session, manifests, sessions, options, metrics, timeProvider);
        var archived = await archiver.RunAsync(manifest, forceSnapshot, cancellationToken);
        return archived switch
        {
            ArchiveManifest stored => await ScheduledStatusForSessionAsync(manifest, stored, session, scheduled, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ScheduledArchiveOutcome> ScheduledStatusForSessionAsync(
        ArchiveManifest loaded,
        ArchiveManifest stored,
        ISqlDatabaseSession session,
        bool scheduled,
        CancellationToken cancellationToken)
    {
        var measured = await StatusForSessionAsync(stored, session, cancellationToken);
        return measured switch
        {
            BackupStatus status => ScheduledStatus(loaded, stored, status, session, scheduled),
            ContinuityNotFound missing => missing,
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private ScheduledArchiveStatus ScheduledStatus(
        ArchiveManifest loaded,
        ArchiveManifest stored,
        BackupStatus status,
        ISqlDatabaseSession session,
        bool scheduled)
    {
        var localBehind = !session.CurrentBookmark.Dominates(status.ArchivedThrough);
        var peerRewindPending = scheduled && IsPeerRewindPending(session.DatabaseId, loaded, stored);
        return new ScheduledArchiveStatus(status, localBehind || peerRewindPending);
    }

    private bool IsPeerRewindPending(Ulid databaseId, ArchiveManifest loaded, ArchiveManifest stored)
    {
        if (!_pendingPeerRewinds.TryGetValue(databaseId, out var pending))
        {
            return false;
        }

        if (timeProvider.GetUtcNow() >= pending.ExpiresAtUtc)
        {
            _ = _pendingPeerRewinds.TryRemove(new KeyValuePair<Ulid, PendingPeerRewind>(databaseId, pending));
            return false;
        }

        var stillPresent = pending.PreRewindObjects is { } marks
            ? ContainsAnyMark(loaded, marks) && ContainsAnyMark(stored, marks)
            : HasHistoryBeyond(loaded, pending.RewoundTo) && HasHistoryBeyond(stored, pending.RewoundTo);
        if (!stillPresent)
        {
            _ = _pendingPeerRewinds.TryRemove(new KeyValuePair<Ulid, PendingPeerRewind>(databaseId, pending));
            return false;
        }

        return true;
    }

    private static HashSet<RewindObjectMark> PreRewindMarksBeyond(ArchiveManifest manifest, CausalBookmark rewoundTo)
    {
        HashSet<RewindObjectMark> marks = [];
        for (var index = 0; index < manifest.Segments.Count; index++)
        {
            var segment = manifest.Segments[index];
            if (segment.LastSequence > rewoundTo.Get(segment.Origin))
            {
                _ = marks.Add(new RewindObjectMark(segment.Key, segment.SealedAtUtc));
            }
        }

        var snapshots = ManifestCodec.Snapshots(manifest);
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (!rewoundTo.Dominates(snapshot.Bookmark))
            {
                _ = marks.Add(new RewindObjectMark(snapshot.Archived.Key, snapshot.Archived.TakenAtUtc));
            }
        }

        return marks;
    }

    private static bool ContainsAnyMark(ArchiveManifest manifest, HashSet<RewindObjectMark> marks)
    {
        for (var index = 0; index < manifest.Segments.Count; index++)
        {
            var segment = manifest.Segments[index];
            if (marks.Contains(new RewindObjectMark(segment.Key, segment.SealedAtUtc)))
            {
                return true;
            }
        }

        for (var index = 0; index < manifest.Snapshots.Count; index++)
        {
            var snapshot = manifest.Snapshots[index];
            if (marks.Contains(new RewindObjectMark(snapshot.Key, snapshot.TakenAtUtc)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasHistoryBeyond(ArchiveManifest manifest, CausalBookmark rewoundTo)
    {
        for (var index = 0; index < manifest.Segments.Count; index++)
        {
            var segment = manifest.Segments[index];
            if (segment.LastSequence > rewoundTo.Get(segment.Origin))
            {
                return true;
            }
        }

        var snapshots = ManifestCodec.Snapshots(manifest);
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (!rewoundTo.Dominates(snapshots[index].Bookmark))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct RewindObjectMark(string Key, DateTimeOffset Timestamp);

    private sealed record PendingPeerRewind(CausalBookmark RewoundTo, HashSet<RewindObjectMark>? PreRewindObjects, DateTimeOffset ExpiresAtUtc);

    private async ValueTask<BackupStatusOutcome> StatusAsync(ArchiveManifest manifest, Ulid databaseId, CancellationToken cancellationToken)
    {
        var opened = await sessions.OpenAsync(databaseId, cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession session => await StatusForOpenSessionAsync(manifest, session, cancellationToken),
            ContinuitySessionMissing when manifest.IsEmpty => new ContinuityNotFound("database has no local files or archived restore points"),
            ContinuitySessionMissing => Status(manifest, 0, null),
            ContinuitySessionUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
        };
    }

    private async ValueTask<BackupStatusOutcome> StatusForOpenSessionAsync(ArchiveManifest manifest, ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        await using (session)
        {
            return await StatusForSessionAsync(manifest, session, cancellationToken);
        }
    }

    private async ValueTask<BackupStatusOutcome> StatusForSessionAsync(ArchiveManifest manifest, ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        var checkedAt = timeProvider.GetUtcNow();
        var current = session.CurrentBookmark;
        var snapshotFloor = ArchiveRetention.SnapshotFloor(manifest);
        var pending = 0;
        foreach (var (origin, _) in current.Entries)
        {
            var listed = await session.ListSealedSegmentsAsync(origin, manifest.ArchivedThrough(origin, snapshotFloor), cancellationToken);
            var (count, busy) = Pending(listed);
            if (busy is not null)
            {
                return new ContinuityUnavailable(busy.Message, "storage_busy");
            }

            pending += count;
        }

        return Status(manifest, pending, new ArchiveProof(current, checkedAt));
    }

    private static (int Count, StorageBusy? Busy) Pending(ListSegmentsOutcome listed) => listed switch
    {
        IReadOnlyList<CommitLogSegment> segments => (segments.Count, null),
        StorageBusy busy => (0, busy),
    };

    private static BackupStatusOutcome Status(ArchiveManifest manifest, int pendingSegments, ArchiveProof? proof)
    {
        ArchivedSnapshot? latestArchived = null;
        CausalBookmark? latestBookmark = null;
        var earliestPoint = DateTimeOffset.MaxValue;
        var totalBytes = 0L;
        for (var index = 0; index < manifest.Snapshots.Count; index++)
        {
            var snapshot = manifest.Snapshots[index];
            totalBytes += snapshot.SizeBytes;
            if (!CausalBookmark.TryDecode(snapshot.Bookmark, out var decodedBookmark))
            {
                continue;
            }

            if (snapshot.TakenAtUtc < earliestPoint)
            {
                earliestPoint = snapshot.TakenAtUtc;
            }

            if (latestArchived is null || snapshot.TakenAtUtc > latestArchived.TakenAtUtc)
            {
                latestArchived = snapshot;
                latestBookmark = decodedBookmark;
            }
        }

        if (latestArchived is null || latestBookmark is null || !Ulid.TryParse(manifest.DatabaseId, out var databaseId))
        {
            return new ContinuityNotFound("no restore point has been archived for this database yet", "backup_pending");
        }

        string? singleOrigin = null;
        var singleSequence = 0L;
        var bookmarkUnchanged = true;
        Dictionary<string, long>? through = null;
        if (latestBookmark.Entries.Count == 1)
        {
            var entry = latestBookmark.Entries[0];
            singleOrigin = entry.Key;
            singleSequence = entry.Value;
        }
        else if (latestBookmark.Entries.Count > 1)
        {
            through = new Dictionary<string, long>(latestBookmark.Entries.Count, StringComparer.Ordinal);
            foreach (var (origin, sequence) in latestBookmark.Entries)
            {
                through[origin] = sequence;
            }
        }

        var latestPoint = latestArchived.TakenAtUtc;
        for (var index = 0; index < manifest.Segments.Count; index++)
        {
            var segment = manifest.Segments[index];
            totalBytes += segment.SizeBytes;
            if (through is null)
            {
                if (singleOrigin is null)
                {
                    if (segment.LastSequence > 0)
                    {
                        singleOrigin = segment.Origin;
                        singleSequence = segment.LastSequence;
                        bookmarkUnchanged = false;
                    }
                }
                else if (string.Equals(singleOrigin, segment.Origin, StringComparison.Ordinal))
                {
                    if (segment.LastSequence > singleSequence)
                    {
                        singleSequence = segment.LastSequence;
                        bookmarkUnchanged = false;
                    }
                }
                else if (segment.LastSequence > 0)
                {
                    through = new Dictionary<string, long>(2, StringComparer.Ordinal)
                    {
                        [singleOrigin] = singleSequence,
                        [segment.Origin] = segment.LastSequence,
                    };
                    bookmarkUnchanged = false;
                }
            }
            else
            {
                ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(through, segment.Origin, out _);
                if (segment.LastSequence > current)
                {
                    current = segment.LastSequence;
                }
            }

            if (segment.LastCommittedAtUtc > latestPoint)
            {
                latestPoint = segment.LastCommittedAtUtc;
            }
        }

        var archivedThrough = through is not null
            ? CausalBookmark.Create(through)
            : bookmarkUnchanged
                ? latestBookmark
                : singleOrigin is not null
                    ? CausalBookmark.ForRegion(singleOrigin, singleSequence)
                    : CausalBookmark.Empty;
        if (proof is { } proven && proven.CheckedAtUtc > latestPoint && archivedThrough.Dominates(proven.Bookmark))
        {
            latestPoint = proven.CheckedAtUtc;
        }

        return new BackupStatus(
            databaseId,
            archivedThrough,
            earliestPoint,
            latestPoint,
            latestArchived.TakenAtUtc,
            totalBytes,
            pendingSegments);
    }

    private async ValueTask<FinishedOperation> FinishAsync(OperationWork work, string? failure, bool record, CancellationToken cancellationToken)
    {
        var state = failure is null ? RestoreOperationState.Succeeded : RestoreOperationState.Failed;
        var operation = Transition(work.OperationId, state, failure);
        var kind = KindLabel(work.Kind);
        metrics.OperationFinished(kind, state);
        var recordFailure = record && manifests.IsEnabled ? await RecordAsync(work, operation, kind, cancellationToken) : null;
        return new FinishedOperation(operation, kind, recordFailure);
    }

    private async ValueTask<string?> RecordAsync(OperationWork work, RestoreOperation operation, string kind, CancellationToken cancellationToken)
    {
        using var lease = await manifests.AcquireAsync(work.TargetDatabaseId, cancellationToken);
        var archived = new ArchivedOperation(
            operation.OperationId.ToString(),
            kind,
            operation.SourceDatabaseId.ToString(),
            operation.TargetDatabaseId.ToString(),
            ManifestCodec.StateLabel(operation.State),
            operation.StartedAtUtc,
            operation.UpdatedAtUtc,
            operation.FailureMessage);
        var saved = await manifests.UpdateAsync(
            work.TargetDatabaseId,
            current => current with
            {
                Operations = [.. current.Operations.Where(existing => existing.OperationId != archived.OperationId).TakeLast(options.RetainedOperations - 1), archived],
            },
            cancellationToken);
        return saved switch
        {
            ArchiveManifest => null,
            ContinuityUnavailable unavailable => unavailable.Message,
        };
    }

    private RestoreOperation Transition(Ulid operationId, RestoreOperationState state, string? failure)
    {
        lock (_gate)
        {
            var current = _operations[operationId];
            var updated = current with { State = state, UpdatedAtUtc = timeProvider.GetUtcNow(), FailureMessage = failure };
            _operations[operationId] = updated;
            if (state is RestoreOperationState.Succeeded or RestoreOperationState.Failed)
            {
                _ = _claimed.Remove(updated.TargetDatabaseId);
                _finished.Enqueue(operationId);
                while (_finished.Count > options.RetainedOperations)
                {
                    _ = _operations.Remove(_finished.Dequeue());
                }
            }

            return updated;
        }
    }

    private static string KindLabel(OperationKind kind) => kind switch
    {
        OperationKind.Restore => "restore",
        OperationKind.Branch => "branch",
        OperationKind.Rewind => "rewind",
        _ => throw new UnreachableException(),
    };
}
