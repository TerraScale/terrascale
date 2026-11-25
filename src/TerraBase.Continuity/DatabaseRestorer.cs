using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraBase.Storage;

namespace TerraBase.Continuity;

internal sealed record OperationFailed(string Message, bool ArchiveMayHelp = false);

internal readonly union ContinuityCopyOutcome(CausalBookmark, OperationFailed);

public sealed class DatabaseRestorer(
    ContinuitySessions sessions,
    StorageEngineOptions storage,
    ArchiveManifests manifests,
    TimeProvider timeProvider,
    ILogger<DatabaseRestorer> logger)
{
    private const int ScanPageSize = 512;
    private const string RewoundDirectoryName = "rewound";

    private readonly StorageLayout _layout = new(storage);

    internal StorageLayout Layout => _layout;

    internal ContinuityFileOutcome RecoverStaging()
    {
        var listed = ContinuityFiles.ListDirectories(_layout.StagingRoot);
        return listed switch
        {
            IReadOnlyList<string> stagings => RecoverAll(stagings),
            ContinuityIoFailure failure => failure,
        };
    }

    internal async ValueTask<ContinuityCopyOutcome> CopyAsync(Ulid operationId, Ulid sourceId, Ulid targetId, RestoreTarget target, CancellationToken cancellationToken)
    {
        var resolved = await sessions.ResolveKeyAsync(targetId, cancellationToken);
        using var lease = await manifests.AcquireAsync(sourceId, cancellationToken);
        return resolved switch
        {
            ContinuityRawKey targetKey => await BuildAsync(operationId, sourceId, targetId, targetKey.Bytes, target, cancellationToken),
            ContinuityDatabaseKeyMissing missing => new OperationFailed(missing.Message),
        };
    }

    internal async ValueTask<ContinuityCopyOutcome> RewindAsync(
        Ulid operationId,
        Ulid databaseId,
        RestoreTarget target,
        bool notifyPeers,
        CancellationToken cancellationToken)
    {
        if (!_layout.Exists(databaseId))
        {
            if (!notifyPeers)
            {
                await sessions.Catalog.DatabaseReplacedAsync(databaseId, null, cancellationToken);
                return target switch
                {
                    CausalBookmark bookmark => bookmark,
                    DateTimeOffset => CausalBookmark.Empty,
                };
            }

            return new OperationFailed("database is not stored on this node");
        }

        var temporary = Ulid.NewUlid(timeProvider.GetUtcNow());
        var resolved = await sessions.ResolveKeyAsync(databaseId, cancellationToken);
        return resolved switch
        {
            ContinuityRawKey key => await Built(await BuildAsync(operationId, databaseId, temporary, key.Bytes, target, cancellationToken), operationId, databaseId, temporary, key.Bytes, notifyPeers, cancellationToken),
            ContinuityDatabaseKeyMissing missing => new OperationFailed(missing.Message),
        };
    }

    private async ValueTask<ContinuityCopyOutcome> Built(
        ContinuityCopyOutcome built,
        Ulid operationId,
        Ulid databaseId,
        Ulid temporary,
        ReadOnlyMemory<byte> key,
        bool notifyPeers,
        CancellationToken cancellationToken) => built switch
        {
            CausalBookmark bookmark => await SwapAsync(operationId, databaseId, temporary, key, bookmark, notifyPeers, cancellationToken),
            OperationFailed failed => failed,
        };

    private ContinuityFileOutcome RecoverAll(IReadOnlyList<string> stagings)
    {
        foreach (var staging in stagings)
        {
            var finished = FinishInterruptedSwap(Path.Combine(staging, RewoundDirectoryName));
            var recovered = finished switch
            {
                ContinuityFilesChanged => ContinuityFiles.DeleteDirectory(staging),
                ContinuityIoFailure io => io,
            };
            var ioFailure = recovered switch
            {
                ContinuityFilesChanged => (ContinuityIoFailure?)null,
                ContinuityIoFailure io => io,
            };
            if (ioFailure is not null)
            {
                return ioFailure;
            }
        }

        return ContinuityFiles.DeleteDirectory(_layout.StagingRoot);
    }

    private ContinuityFileOutcome FinishInterruptedSwap(string rewound)
    {
        var listed = ContinuityFiles.ListFiles(rewound, "*.db");
        return listed switch
        {
            IReadOnlyList<string> { Count: 1 } files when Ulid.TryParse(Path.GetFileNameWithoutExtension(files[0]), out var databaseId) && !_layout.DirectoryExists(databaseId) =>
                ContinuityFiles.MoveDirectory(rewound, _layout.DatabaseDirectory(databaseId)),
            IReadOnlyList<string> => new ContinuityFilesChanged(),
            ContinuityIoFailure failure => failure,
        };
    }

    private async ValueTask<ContinuityCopyOutcome> BuildAsync(
        Ulid operationId,
        Ulid sourceId,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        if (_layout.DirectoryExists(targetId))
        {
            return new OperationFailed("target database already exists on this node");
        }

        var resolved = await sessions.ResolveKeyAsync(sourceId, cancellationToken);
        return resolved switch
        {
            ContinuityRawKey sourceKey => await BuildWithKeyAsync(operationId, sourceId, sourceKey.Bytes, targetId, targetKey, target, cancellationToken),
            ContinuityDatabaseKeyMissing missing => new OperationFailed(missing.Message),
        };
    }

    private async ValueTask<ContinuityCopyOutcome> BuildWithKeyAsync(
        Ulid operationId,
        Ulid sourceId,
        ReadOnlyMemory<byte> sourceKey,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        var opened = await sessions.OpenAsync(sourceId, sourceKey, cancellationToken);
        var local = opened switch
        {
            ISqlDatabaseSession session => await BranchLocalAsync(session, targetId, targetKey, target, cancellationToken),
            ContinuitySessionMissing => new OperationFailed("source database is not stored on this node", ArchiveMayHelp: true),
            ContinuitySessionUnavailable unavailable => new OperationFailed(unavailable.Message),
        };
        return local switch
        {
            CausalBookmark bookmark => bookmark,
            OperationFailed { ArchiveMayHelp: true } failed when manifests.IsEnabled =>
                await BuildFromArchiveAsync(operationId, sourceId, sourceKey, targetId, targetKey, target, cancellationToken),
            OperationFailed failed => failed,
        };
    }

    private async ValueTask<ContinuityCopyOutcome> BranchLocalAsync(
        ISqlDatabaseSession source,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        await using (source)
        {
            var resolved = await ResolveAsync(source, target, CausalBookmark.Empty, cancellationToken);
            return resolved switch
            {
                CausalBookmark at => await BranchAsync(sessions.Engine, source.DatabaseId, targetId, targetKey, at, cancellationToken),
                OperationFailed failed => failed,
            };
        }
    }

    private async ValueTask<ContinuityCopyOutcome> BranchAsync(
        ISqlStorageEngine engine,
        Ulid sourceId,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        CausalBookmark at,
        CancellationToken cancellationToken)
    {
        var branched = await engine.BranchAsync(new DatabaseBranchRequest(sourceId, targetId, targetKey, at), cancellationToken);
        return branched switch
        {
            ISqlDatabaseSession branch => await CloseBranchAsync(branch),
            DatabaseNotFound missing => new OperationFailed(missing.Message, ArchiveMayHelp: true),
            InvalidDatabaseRequest invalid => new OperationFailed(invalid.Message, ArchiveMayHelp: true),
            DatabaseAlreadyExists exists => new OperationFailed(exists.Message),
            StorageBusy busy => new OperationFailed(busy.Message),
        };
    }

    private async ValueTask<ContinuityCopyOutcome> CloseBranchAsync(ISqlDatabaseSession branch)
    {
        if (SqliteStorageEngine.TakeRecoveredQuarantined(branch) is > 0 and var quarantined)
        {
            ContinuityLog.RecoveredCommitsQuarantined(logger, branch.DatabaseId, quarantined, branch.CurrentBookmark);
        }

        var bookmark = branch.CurrentBookmark;
        await branch.DisposeAsync();
        return bookmark;
    }

    private async ValueTask<ContinuityCopyOutcome> BuildFromArchiveAsync(
        Ulid operationId,
        Ulid sourceId,
        ReadOnlyMemory<byte> sourceKey,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        var loaded = await manifests.ReadCurrentAsync(sourceId, cancellationToken);
        return loaded switch
        {
            ArchiveManifest manifest => await BuildFromManifestAsync(operationId, manifest, sourceId, sourceKey, targetId, targetKey, target, cancellationToken),
            ContinuityUnavailable unavailable => new OperationFailed(unavailable.Message),
        };
    }

    private async ValueTask<ContinuityCopyOutcome> BuildFromManifestAsync(
        Ulid operationId,
        ArchiveManifest manifest,
        Ulid sourceId,
        ReadOnlyMemory<byte> sourceKey,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        if (ChooseSnapshot(manifest, target) is not { } snapshot)
        {
            return new OperationFailed("no archived snapshot precedes the requested restore point");
        }

        var staging = _layout.StagingDirectory(operationId);
        if (await DownloadAsync(staging, sourceId, manifest, snapshot, target, cancellationToken) is { } downloadFailure)
        {
            Cleanup(staging);
            return downloadFailure;
        }

        var staged = await BranchInStagingAsync(staging, sourceId, sourceKey, targetId, targetKey, snapshot.Bookmark, target, cancellationToken);
        var placed = staged switch
        {
            CausalBookmark bookmark => Place(ContinuityFiles.MoveDirectory(StorageLayout.DatabaseDirectoryIn(staging, targetId), _layout.DatabaseDirectory(targetId)), bookmark),
            OperationFailed failed => failed,
        };
        Cleanup(staging);
        return placed;

        static ContinuityCopyOutcome Place(ContinuityFileOutcome moved, CausalBookmark bookmark) => moved switch
        {
            ContinuityFilesChanged => bookmark,
            ContinuityIoFailure failure => new OperationFailed(failure.Message),
        };
    }

    private async ValueTask<ContinuityCopyOutcome> BranchInStagingAsync(
        string staging,
        Ulid sourceId,
        ReadOnlyMemory<byte> sourceKey,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        CausalBookmark floor,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        var engine = new SqliteStorageEngine(storage with { RootDirectory = staging }, timeProvider);
        await using (engine)
        {
            var opened = await engine.OpenAsync(new DatabaseOpenRequest(sourceId, sourceKey), cancellationToken);
            return opened switch
            {
                ISqlDatabaseSession source => await BranchStagedAsync(engine, source, targetId, targetKey, floor, target, cancellationToken),
                DatabaseNotFound missing => new OperationFailed(missing.Message),
                InvalidDatabaseRequest invalid => new OperationFailed(invalid.Message),
                StorageCorrupted corrupted => new OperationFailed(string.Concat("archived history is corrupted: ", corrupted.Message)),
                StorageBusy busy => new OperationFailed(busy.Message),
            };
        }
    }

    private async ValueTask<ContinuityCopyOutcome> BranchStagedAsync(
        SqliteStorageEngine engine,
        ISqlDatabaseSession source,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        CausalBookmark floor,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        await using (source)
        {
            var resolved = await ResolveAsync(source, target, floor, cancellationToken);
            var branched = resolved switch
            {
                CausalBookmark at when source.CurrentBookmark.Dominates(at) =>
                    await BranchAsync(engine, source.DatabaseId, targetId, targetKey, at, cancellationToken),
                CausalBookmark => new OperationFailed("archived commit log ends before the requested bookmark"),
                OperationFailed failed => failed,
            };
            return branched switch
            {
                CausalBookmark bookmark => bookmark,
                OperationFailed failed => failed with { ArchiveMayHelp = false },
            };
        }
    }

    private async ValueTask<OperationFailed?> DownloadAsync(
        string staging,
        Ulid sourceId,
        ArchiveManifest manifest,
        DecodedSnapshot snapshot,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        var databaseFile = StorageLayout.DatabaseFileIn(staging, sourceId);
        var snapshotFile = StorageLayout.SnapshotFileIn(staging, sourceId, Ulid.NewUlid(snapshot.Archived.TakenAtUtc));
        if (Local(ContinuityFiles.CreateDirectory(StorageLayout.SnapshotDirectoryIn(staging, sourceId))) is { } created)
        {
            return created;
        }

        var downloaded = await manifests.Store.DownloadToFileAsync(snapshot.Archived.Key, databaseFile, cancellationToken);
        if (Downloaded(downloaded) is { } fetched)
        {
            return fetched;
        }

        if (Local(ContinuityFiles.CopyFile(databaseFile, snapshotFile)) is { } copied)
        {
            return copied;
        }

        foreach (var group in manifest.Segments.GroupBy(static segment => segment.Origin, StringComparer.Ordinal))
        {
            if (group.Key is not { } origin || !StorageEngineOptions.IsValidRegion(origin))
            {
                return new OperationFailed("archived commit log segment has an invalid origin region");
            }

            var baseline = snapshot.Bookmark.Get(origin);
            var history = Chain([.. group], 1);
            var chain = history.Count > 0 && history[^1].LastSequence >= baseline ? history : Chain([.. group], baseline + 1);
            foreach (var segment in chain)
            {
                if (segment.LastSequence > baseline && !Needed(segment, target))
                {
                    break;
                }

                var path = StorageLayout.SegmentFileIn(staging, sourceId, origin, segment.FirstSequence, segment.LastSequence);
                var segmentDownload = await manifests.Store.DownloadToFileAsync(segment.Key, path, segment.SizeBytes, cancellationToken);
                if (Downloaded(segmentDownload, segment.SizeBytes) is { } failure)
                {
                    return failure;
                }
            }
        }

        return null;
    }

    private async ValueTask<ContinuityCopyOutcome> SwapAsync(
        Ulid operationId,
        Ulid databaseId,
        Ulid temporary,
        ReadOnlyMemory<byte> key,
        CausalBookmark bookmark,
        bool notifyPeers,
        CancellationToken cancellationToken)
    {
        var staging = _layout.StagingDirectory(operationId);
        var rewound = Path.Combine(staging, RewoundDirectoryName);
        var temporaryDirectory = _layout.DatabaseDirectory(temporary);
        if (Local(ContinuityFiles.CreateDirectory(rewound)) is { } created)
        {
            return created;
        }

        if (await CopyRewoundHistoryAsync(temporary, key, rewound, databaseId, bookmark, cancellationToken) is { } historyFailure)
        {
            Cleanup(staging);
            return historyFailure;
        }

        var prefix = string.Concat(temporary.ToString(), ".db");
        if (MoveDatabaseFiles(temporaryDirectory, prefix, rewound, string.Concat(databaseId.ToString(), ".db")) is { } moved)
        {
            return moved;
        }

        if (Local(ContinuityFiles.DeleteDirectory(temporaryDirectory)) is { } removed)
        {
            return removed;
        }

        var deleted = await sessions.Engine.DeleteAsync(databaseId, cancellationToken);
        var deleteFailure = deleted switch
        {
            DatabaseDeleted or DatabaseNotFound => (OperationFailed?)null,
            StorageBusy busy => new OperationFailed(string.Concat("current database could not be closed for the rewind: ", busy.Message)),
        };
        if (deleteFailure is not null)
        {
            return deleteFailure;
        }

        if (Local(ContinuityFiles.MoveDirectory(rewound, _layout.DatabaseDirectory(databaseId))) is { } placed)
        {
            return placed with { Message = string.Concat(placed.Message, "; rewound files remain in ", rewound) };
        }

        Cleanup(staging);
        await sessions.Catalog.DatabaseReplacedAsync(databaseId, notifyPeers ? bookmark : null, cancellationToken);
        return bookmark;
    }

    private async ValueTask<OperationFailed?> CopyRewoundHistoryAsync(
        Ulid temporary,
        ReadOnlyMemory<byte> key,
        string destination,
        Ulid databaseId,
        CausalBookmark bookmark,
        CancellationToken cancellationToken)
    {
        var opened = await sessions.OpenAsync(temporary, key, cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession session => await CopyAndCloseAsync(session, destination, databaseId, key, bookmark),
            ContinuitySessionMissing => null,
            ContinuitySessionUnavailable unavailable => new OperationFailed(unavailable.Message),
        };
    }

    private static async ValueTask<OperationFailed?> CopyAndCloseAsync(
        ISqlDatabaseSession session,
        string destination,
        Ulid databaseId,
        ReadOnlyMemory<byte> key,
        CausalBookmark bookmark)
    {
        await using (session)
        {
            return SqliteStorageEngine.CopyHistory(session, destination, databaseId, key, bookmark) is { } busy
                ? new OperationFailed(busy.Message)
                : null;
        }
    }

    private static OperationFailed? MoveDatabaseFiles(string directory, string prefix, string destination, string replacement)
    {
        var listed = ContinuityFiles.ListFiles(directory, string.Concat(prefix, "*"));
        return listed switch
        {
            IReadOnlyList<string> { Count: 0 } => new OperationFailed("rewound database file is missing"),
            IReadOnlyList<string> files => MoveAll(files, prefix, destination, replacement),
            ContinuityIoFailure failure => new OperationFailed(failure.Message),
        };
    }

    private static OperationFailed? MoveAll(IReadOnlyList<string> files, string prefix, string destination, string replacement)
    {
        foreach (var file in files)
        {
            var suffix = Path.GetFileName(file)[prefix.Length..];
            if (Local(ContinuityFiles.MoveFile(file, Path.Combine(destination, string.Concat(replacement, suffix)))) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static async ValueTask<ContinuityCopyOutcome> ResolveAsync(ISqlDatabaseSession session, RestoreTarget target, CausalBookmark floor, CancellationToken cancellationToken) =>
        target switch
        {
            DateTimeOffset at => await ResolveTimeAsync(session, at, floor, cancellationToken),
            CausalBookmark bookmark => bookmark,
        };

    private static async ValueTask<ContinuityCopyOutcome> ResolveTimeAsync(ISqlDatabaseSession session, DateTimeOffset at, CausalBookmark floor, CancellationToken cancellationToken)
    {
        var limit = at.ToUnixTimeMilliseconds();
        Dictionary<string, long> resolved = new(StringComparer.Ordinal);
        foreach (var (origin, sequence) in floor.Entries)
        {
            resolved[origin] = sequence;
        }

        foreach (var (origin, current) in session.CurrentBookmark.Entries)
        {
            var scan = new TimeScan(origin, limit, floor.Get(origin) + 1);
            while (scan.Continues(current))
            {
                var read = await session.ReadCommitLogAsync(origin, scan.Next, ScanPageSize, cancellationToken);
                scan.Apply(read);
            }

            if (scan.Failure is { } failure)
            {
                return failure;
            }

            if (scan.Last > resolved.GetValueOrDefault(origin))
            {
                resolved[origin] = scan.Last;
            }
        }

        var bookmark = CausalBookmark.Create(resolved);
        return bookmark.IsEmpty ? new OperationFailed("no committed transaction precedes the requested restore point") : bookmark;
    }

    private static DecodedSnapshot? ChooseSnapshot(ArchiveManifest manifest, RestoreTarget target)
    {
        var ordered = ManifestCodec.Snapshots(manifest).OrderBy(static snapshot => snapshot.Archived.TakenAtUtc).ToArray();
        return target switch
        {
            DateTimeOffset at => ordered.LastOrDefault(snapshot => snapshot.Archived.TakenAtUtc <= at),
            CausalBookmark bookmark => bookmark.IsEmpty ? ordered.LastOrDefault() : ordered.LastOrDefault(snapshot => bookmark.Dominates(snapshot.Bookmark)),
        };
    }

    private static List<ArchivedSegment> Chain(IReadOnlyList<ArchivedSegment> segments, long from)
    {
        List<ArchivedSegment> chain = [];
        var expected = from;
        while (segments
            .Where(segment => chain.Count == 0 ? segment.FirstSequence <= expected && segment.LastSequence >= expected : segment.FirstSequence == expected)
            .MaxBy(static segment => segment.LastSequence) is { } next)
        {
            chain.Add(next);
            expected = next.LastSequence + 1;
        }

        return chain;
    }

    private static bool Needed(ArchivedSegment segment, RestoreTarget target) => target switch
    {
        DateTimeOffset at => segment.FirstCommittedAtUtc <= at,
        CausalBookmark bookmark => bookmark.IsEmpty || segment.FirstSequence <= bookmark.Get(segment.Origin),
    };

    private static OperationFailed? Local(ContinuityFileOutcome result) => result switch
    {
        ContinuityFilesChanged => null,
        ContinuityIoFailure failure => new OperationFailed(failure.Message),
    };

    private static OperationFailed? Downloaded(ArchiveDownloadOutcome result, long? expectedBytes = null) => result switch
    {
        long length when expectedBytes is { } expected && length != expected => new OperationFailed("archived segment size does not match its manifest"),
        long => null,
        ArchiveObjectMissing missing => new OperationFailed(string.Concat("archived object is missing: ", missing.Message)),
        ArchiveObjectTooLarge oversized => new OperationFailed(string.Concat("archived segment exceeds the download limit: ", oversized.Key)),
        ArchiveUnavailable unavailable => new OperationFailed(unavailable.Message),
        ArchiveTimedOut timeout => new OperationFailed(timeout.Message),
    };

    internal void Cleanup(string staging)
    {
        var cleaned = ContinuityFiles.DeleteDirectory(staging);
        var reason = cleaned switch
        {
            ContinuityFilesChanged => (string?)null,
            ContinuityIoFailure failure => failure.Message,
        };
        if (reason is not null)
        {
            ContinuityLog.StagingCleanupFailed(logger, reason);
        }
    }

    private sealed class TimeScan(string origin, long limit, long next)
    {
        private bool _complete;

        public long Next { get; private set; } = next;

        public long Last { get; private set; }

        public OperationFailed? Failure { get; private set; }

        public bool Continues(long current) => !_complete && Failure is null && Next <= current;

        public void Apply(ReadCommitLogOutcome read) => Failure = read switch
        {
            CommitLogPage page => Accept(page),
            CommitLogGap gap => new OperationFailed(string.Concat("commit log for origin ", origin, " no longer holds history before sequence ", gap.OldestAvailable.ToString(CultureInfo.InvariantCulture)), ArchiveMayHelp: true),
            StorageCorrupted corrupted => new OperationFailed(corrupted.Message),
            StorageBusy busy => new OperationFailed(busy.Message),
        };

        private OperationFailed? Accept(CommitLogPage page)
        {
            foreach (var entry in page.Entries)
            {
                if (entry.Hlc.WallMs > limit)
                {
                    _complete = true;
                    return null;
                }

                Last = entry.Sequence;
            }

            if (page.Entries.Count == 0)
            {
                _complete = true;
            }

            Next = page.NextSequence;
            return null;
        }
    }
}
