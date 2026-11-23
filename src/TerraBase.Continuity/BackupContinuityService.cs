using System.Diagnostics;
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

public sealed class BackupContinuityService(
    ContinuitySessions sessions,
    ArchiveManifests manifests,
    DatabaseRestorer restorer,
    ContinuityOptions options,
    ContinuityMetrics metrics,
    TimeProvider timeProvider) : IBackupContinuity
{
    private static readonly Unavailable Disabled = new("continuous backup is disabled on this node", "backup_disabled");

    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, RestoreOperation> _operations = [];
    private readonly Queue<Ulid> _finished = new();
    private readonly HashSet<Ulid> _claimed = [];
    private readonly Channel<OperationWork> _queue = Channel.CreateBounded<OperationWork>(new BoundedChannelOptions(options.OperationQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    public bool IsBackupEnabled => manifests.IsEnabled;

    public async ValueTask<Result<BackupStatus, NotFound, Unavailable>> GetStatusAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (!manifests.IsEnabled)
        {
            return Disabled;
        }

        return await manifests.LoadAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ArchiveManifest manifest => await StatusAsync(manifest, databaseId, cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => unavailable,
        };
    }

    public ValueTask<Result<BackupStatus, NotFound, Unavailable>> ArchiveNowAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        ArchiveAsync(databaseId, forceSnapshot: true, cancellationToken);

    public async ValueTask<Result<BackupStatus, NotFound, Unavailable>> ArchiveAsync(Ulid databaseId, bool forceSnapshot, CancellationToken cancellationToken)
    {
        if (!manifests.IsEnabled)
        {
            return Disabled;
        }

        using var lease = await manifests.AcquireAsync(databaseId, cancellationToken).ConfigureAwait(false);
        return await ArchiveLockedAsync(databaseId, forceSnapshot, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> RestoreAsync(PointInTimeRestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EnqueueCopyAsync(OperationKind.Restore, request.SourceDatabaseId, request.TargetDatabaseId, request.Target, cancellationToken);
    }

    public ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> BranchAsync(BranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EnqueueCopyAsync(OperationKind.Branch, request.SourceDatabaseId, request.BranchDatabaseId, request.Target, cancellationToken);
    }

    public async ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> RewindAsync(Ulid databaseId, RestoreTarget target, CancellationToken cancellationToken)
    {
        if (ValidateTarget(target) is { } invalid)
        {
            return invalid;
        }

        if (!restorer.Layout.Exists(databaseId))
        {
            return new NotFound("database is not stored on this node", "database_not_local");
        }

        return await sessions.ResolveKeyAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ReadOnlyMemory<byte> => Enqueue(OperationKind.Rewind, databaseId, databaseId, target),
            NotFound notFound => notFound,
        };
    }

    public ValueTask<Result<RestoreOperation, NotFound>> GetOperationAsync(Ulid operationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<Result<RestoreOperation, NotFound>>(_operations.TryGetValue(operationId, out var operation)
                ? operation
                : new NotFound("continuity operation not found", "operation_not_found"));
        }
    }

    public async ValueTask<Result<Done, NotFound, Unavailable>> CryptoShredAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        using var lease = await manifests.AcquireAsync(databaseId, cancellationToken).ConfigureAwait(false);
        var local = await sessions.Engine.CryptoShredAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            Done => Shredded(true),
            NotFound => Shredded(false),
            StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
        };
        var remote = manifests.IsEnabled
            ? await manifests.PurgeAsync(databaseId, cancellationToken).ConfigureAwait(false)
            : Shredded(false);
        return (local, remote) switch
        {
            (Unavailable unavailable, _) => unavailable,
            (_, Unavailable unavailable) => unavailable,
            (bool localFound, bool remoteFound) => localFound || remoteFound ? new Done() : new NotFound("database has no local files or archived objects"),
        };

        static Result<bool, Unavailable> Shredded(bool found) => found;
    }

    internal IAsyncEnumerable<OperationWork> PendingOperationsAsync(CancellationToken cancellationToken) => _queue.Reader.ReadAllAsync(cancellationToken);

    internal async ValueTask<FinishedOperation> ExecuteAsync(OperationWork work, CancellationToken cancellationToken)
    {
        Transition(work.OperationId, RestoreOperationState.Running, null);
        var outcome = work.Kind switch
        {
            OperationKind.Restore or OperationKind.Branch => await restorer.CopyAsync(work.OperationId, work.SourceDatabaseId, work.TargetDatabaseId, work.Target, cancellationToken).ConfigureAwait(false),
            OperationKind.Rewind => await RewindLockedAsync(work, cancellationToken).ConfigureAwait(false),
            _ => throw new UnreachableException(),
        };
        return outcome switch
        {
            CausalBookmark => await FinishAsync(work, null, record: true, cancellationToken).ConfigureAwait(false),
            OperationFailed failed => await FailAsync(work, failed.Message, cancellationToken).ConfigureAwait(false),
        };
    }

    internal async ValueTask<FinishedOperation> FaultAsync(OperationWork work, string message) =>
        await FailAsync(work, message, CancellationToken.None).ConfigureAwait(false);

    private async ValueTask<FinishedOperation> FailAsync(OperationWork work, string message, CancellationToken cancellationToken) =>
        work.Kind switch
        {
            OperationKind.Restore or OperationKind.Branch => await sessions.Catalog.DiscardCopyTargetAsync(work.TargetDatabaseId, CancellationToken.None).ConfigureAwait(false) switch
            {
                Done => await FinishAsync(work, message, record: false, cancellationToken).ConfigureAwait(false),
                Unavailable unavailable => await FinishAsync(work, string.Concat(message, "; the target database could not be discarded: ", unavailable.Message), record: true,
                    cancellationToken).ConfigureAwait(false),
            },
            OperationKind.Rewind => await FinishAsync(work, message, record: true, cancellationToken).ConfigureAwait(false),
            _ => throw new UnreachableException(),
        };

    private ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> EnqueueCopyAsync(
        OperationKind kind,
        Ulid sourceId,
        Ulid targetId,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        if (sourceId == targetId)
        {
            return ValueTask.FromResult<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>>(
                new Validation("source and target databases must differ", "same_database", [new ValidationDetail("targetDatabaseId", "distinct")]));
        }

        if (ValidateTarget(target) is { } invalid)
        {
            return ValueTask.FromResult<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>>(invalid);
        }

        if (restorer.Layout.DirectoryExists(targetId))
        {
            return ValueTask.FromResult<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>>(
                new Conflict("target database already exists on this node", "database_exists"));
        }

        return EnqueueResolvedCopyAsync(kind, sourceId, targetId, target, cancellationToken);
    }

    private async ValueTask<Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable>> EnqueueResolvedCopyAsync(
        OperationKind kind,
        Ulid sourceId,
        Ulid targetId,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        var keys = (await sessions.ResolveKeyAsync(sourceId, cancellationToken).ConfigureAwait(false), await sessions.ResolveKeyAsync(targetId, cancellationToken).ConfigureAwait(false));
        return keys switch
        {
            (NotFound notFound, _) => notFound,
            (_, NotFound notFound) => notFound,
            (ReadOnlyMemory<byte>, ReadOnlyMemory<byte>) => await SourceAvailableAsync(sourceId, cancellationToken).ConfigureAwait(false) switch
            {
                Done => Enqueue(kind, sourceId, targetId, target),
                NotFound notFound => notFound,
                Unavailable unavailable => unavailable,
            },
        };
    }

    private async ValueTask<Result<Done, NotFound, Unavailable>> SourceAvailableAsync(Ulid sourceId, CancellationToken cancellationToken)
    {
        if (restorer.Layout.Exists(sourceId))
        {
            return new Done();
        }

        if (!manifests.IsEnabled)
        {
            return new NotFound("source database is not stored on this node and continuous backup is disabled", "database_not_local");
        }

        return await manifests.LoadAsync(sourceId, cancellationToken).ConfigureAwait(false) switch
        {
            ArchiveManifest { Snapshots.Count: > 0 } => new Done(),
            ArchiveManifest => new NotFound("source database has no local files or archived snapshots", "no_restore_point"),
            Unavailable unavailable => unavailable,
        };
    }

    private Result<RestoreOperation, NotFound, Validation, Conflict, Unavailable> Enqueue(OperationKind kind, Ulid sourceId, Ulid targetId, RestoreTarget target)
    {
        var now = timeProvider.GetUtcNow();
        var operation = new RestoreOperation(Ulid.NewUlid(now), sourceId, targetId, RestoreOperationState.Pending, now, now, null);
        lock (_gate)
        {
            if (!_claimed.Add(targetId))
            {
                return new Conflict("another continuity operation is already running for this database", "operation_in_progress");
            }

            if (!_queue.Writer.TryWrite(new OperationWork(operation.OperationId, kind, sourceId, targetId, target)))
            {
                _ = _claimed.Remove(targetId);
                return new Unavailable("the continuity operation queue is full", "operation_queue_full");
            }

            _operations[operation.OperationId] = operation;
        }

        return operation;
    }

    private Validation? ValidateTarget(RestoreTarget target) => target switch
    {
        DateTimeOffset at when at > timeProvider.GetUtcNow() => new Validation("the restore point is in the future", "restore_point_in_future", [new ValidationDetail("target", "past")]),
        DateTimeOffset => null,
        CausalBookmark => null,
    };

    private async ValueTask<Result<CausalBookmark, OperationFailed>> RewindLockedAsync(OperationWork work, CancellationToken cancellationToken)
    {
        using var lease = await manifests.AcquireAsync(work.TargetDatabaseId, cancellationToken).ConfigureAwait(false);
        var rewound = await restorer.RewindAsync(work.OperationId, work.TargetDatabaseId, work.Target, cancellationToken).ConfigureAwait(false);
        return rewound switch
        {
            CausalBookmark bookmark when manifests.IsEnabled => await ReconcileArchiveAsync(work.TargetDatabaseId, bookmark, cancellationToken).ConfigureAwait(false),
            CausalBookmark bookmark => bookmark,
            OperationFailed failed => failed,
        };
    }

    private async ValueTask<Result<CausalBookmark, OperationFailed>> ReconcileArchiveAsync(Ulid databaseId, CausalBookmark rewoundTo, CancellationToken cancellationToken)
    {
        var truncated = await manifests.LoadAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ArchiveManifest manifest => await TruncateAsync(manifest, rewoundTo, cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => unavailable,
        };
        var archived = truncated switch
        {
            Done => await ArchiveLockedAsync(databaseId, forceSnapshot: true, cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => unavailable,
        };
        return archived switch
        {
            BackupStatus => rewoundTo,
            NotFound notFound => new OperationFailed(string.Concat("database was rewound but could not be archived: ", notFound.Message)),
            Unavailable unavailable => new OperationFailed(string.Concat("database was rewound but could not be archived: ", unavailable.Message)),
        };
    }

    private async ValueTask<Result<Done, Unavailable>> TruncateAsync(ArchiveManifest manifest, CausalBookmark rewoundTo, CancellationToken cancellationToken)
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
            return new Done();
        }

        var saved = await manifests.SaveAsync(manifest with { Snapshots = keptSnapshots, Segments = keptSegments }, cancellationToken).ConfigureAwait(false);
        return saved switch
        {
            ArchiveManifest => await manifests.DeleteObjectsAsync(dropped, cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<BackupStatus, NotFound, Unavailable>> ArchiveLockedAsync(Ulid databaseId, bool forceSnapshot, CancellationToken cancellationToken) =>
        await sessions.OpenAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ISqlDatabaseSession session => await ArchiveSessionAsync(session, forceSnapshot, cancellationToken).ConfigureAwait(false),
            NotFound notFound => notFound,
            Unavailable unavailable => unavailable,
        };

    private async ValueTask<Result<BackupStatus, NotFound, Unavailable>> ArchiveSessionAsync(ISqlDatabaseSession session, bool forceSnapshot, CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            var archiver = new SessionArchiver(session, manifests, sessions.Engine, options, metrics, timeProvider);
            return await manifests.LoadAsync(session.DatabaseId, cancellationToken).ConfigureAwait(false) switch
            {
                ArchiveManifest manifest => await archiver.RunAsync(manifest, forceSnapshot, cancellationToken).ConfigureAwait(false) switch
                {
                    ArchiveManifest archived => await StatusForSessionAsync(archived, session, cancellationToken).ConfigureAwait(false),
                    Unavailable unavailable => unavailable,
                },
                Unavailable unavailable => unavailable,
            };
        }
    }

    private async ValueTask<Result<BackupStatus, NotFound, Unavailable>> StatusAsync(ArchiveManifest manifest, Ulid databaseId, CancellationToken cancellationToken) =>
        await sessions.OpenAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ISqlDatabaseSession session => await StatusForOpenSessionAsync(manifest, session, cancellationToken).ConfigureAwait(false),
            NotFound when manifest.IsEmpty => new NotFound("database has no local files or archived restore points"),
            NotFound => Status(manifest, 0),
            Unavailable unavailable => unavailable,
        };

    private static async ValueTask<Result<BackupStatus, NotFound, Unavailable>> StatusForOpenSessionAsync(ArchiveManifest manifest, ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            return await StatusForSessionAsync(manifest, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<Result<BackupStatus, NotFound, Unavailable>> StatusForSessionAsync(ArchiveManifest manifest, ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        var pending = 0;
        foreach (var (origin, _) in session.CurrentBookmark.Entries)
        {
            var (count, busy) = Pending(await session.ListSealedSegmentsAsync(origin, manifest.ArchivedSegmentThrough(origin), cancellationToken).ConfigureAwait(false));
            if (busy is not null)
            {
                return new Unavailable(busy.Message, "storage_busy");
            }

            pending += count;
        }

        return Status(manifest, pending);
    }

    private static (int Count, StorageBusy? Busy) Pending(Result<IReadOnlyList<CommitLogSegment>, StorageBusy> listed) => listed switch
    {
        IReadOnlyList<CommitLogSegment> segments => (segments.Count, null),
        StorageBusy busy => (0, busy),
    };

    private static Result<BackupStatus, NotFound, Unavailable> Status(ArchiveManifest manifest, int pendingSegments)
    {
        var snapshots = ManifestCodec.Snapshots(manifest);
        if (snapshots.Count == 0 || !Ulid.TryParse(manifest.DatabaseId, out var databaseId))
        {
            return new NotFound("no restore point has been archived for this database yet", "backup_pending");
        }

        var latest = snapshots.MaxBy(static snapshot => snapshot.Archived.TakenAtUtc) ?? throw new UnreachableException();
        Dictionary<string, long> through = new(StringComparer.Ordinal);
        foreach (var (origin, sequence) in latest.Bookmark.Entries)
        {
            through[origin] = sequence;
        }

        var latestPoint = latest.Archived.TakenAtUtc;
        foreach (var segment in manifest.Segments)
        {
            if (segment.LastSequence > through.GetValueOrDefault(segment.Origin))
            {
                through[segment.Origin] = segment.LastSequence;
            }

            if (segment.LastCommittedAtUtc > latestPoint)
            {
                latestPoint = segment.LastCommittedAtUtc;
            }
        }

        return new BackupStatus(
            databaseId,
            CausalBookmark.Create(through),
            snapshots.Min(static snapshot => snapshot.Archived.TakenAtUtc),
            latestPoint,
            latest.Archived.TakenAtUtc,
            manifest.Snapshots.Sum(static snapshot => snapshot.SizeBytes) + manifest.Segments.Sum(static segment => segment.SizeBytes),
            pendingSegments);
    }

    private async ValueTask<FinishedOperation> FinishAsync(OperationWork work, string? failure, bool record, CancellationToken cancellationToken)
    {
        var state = failure is null ? RestoreOperationState.Succeeded : RestoreOperationState.Failed;
        var operation = Transition(work.OperationId, state, failure);
        var kind = KindLabel(work.Kind);
        metrics.OperationFinished(kind, state);
        var recordFailure = record && manifests.IsEnabled ? await RecordAsync(work, operation, kind, cancellationToken).ConfigureAwait(false) : null;
        return new FinishedOperation(operation, kind, recordFailure);
    }

    private async ValueTask<string?> RecordAsync(OperationWork work, RestoreOperation operation, string kind, CancellationToken cancellationToken)
    {
        using var lease = await manifests.AcquireAsync(work.TargetDatabaseId, cancellationToken).ConfigureAwait(false);
        var archived = new ArchivedOperation(
            operation.OperationId.ToString(),
            kind,
            operation.SourceDatabaseId.ToString(),
            operation.TargetDatabaseId.ToString(),
            ManifestCodec.StateLabel(operation.State),
            operation.StartedAtUtc,
            operation.UpdatedAtUtc,
            operation.FailureMessage);
        var saved = await manifests.LoadAsync(work.TargetDatabaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ArchiveManifest manifest => await manifests.SaveAsync(
                manifest with { Operations = [.. manifest.Operations.TakeLast(options.RetainedOperations - 1), archived] },
                cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => unavailable,
        };
        return saved switch
        {
            ArchiveManifest => null,
            Unavailable unavailable => unavailable.Message,
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
