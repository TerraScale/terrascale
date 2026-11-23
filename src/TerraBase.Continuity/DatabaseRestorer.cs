using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraBase.Storage;

namespace TerraBase.Continuity;

internal sealed record OperationFailed(string Message, bool ArchiveMayHelp = false);

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

    internal Result<Done, Unavailable> RecoverStaging() => ContinuityFiles.ListDirectories(_layout.StagingRoot) switch
    {
        IReadOnlyList<string> stagings => RecoverAll(stagings),
        Unavailable unavailable => unavailable,
    };

    internal async ValueTask<Result<CausalBookmark, OperationFailed>> CopyAsync(Ulid operationId, Ulid sourceId, Ulid targetId, RestoreTarget target, CancellationToken cancellationToken) =>
        await sessions.ResolveKeyAsync(targetId, cancellationToken).ConfigureAwait(false) switch
        {
            ReadOnlyMemory<byte> targetKey => await BuildAsync(operationId, sourceId, targetId, targetKey, target, cancellationToken).ConfigureAwait(false),
            NotFound notFound => new OperationFailed(notFound.Message ?? "target database key not found"),
        };

    internal async ValueTask<Result<CausalBookmark, OperationFailed>> RewindAsync(
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
                await sessions.Catalog.DatabaseReplacedAsync(databaseId, null, cancellationToken).ConfigureAwait(false);
                return target switch
                {
                    CausalBookmark bookmark => bookmark,
                    DateTimeOffset => CausalBookmark.Empty,
                };
            }

            return new OperationFailed("database is not stored on this node");
        }

        var temporary = Ulid.NewUlid(timeProvider.GetUtcNow());
        return await sessions.ResolveKeyAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            ReadOnlyMemory<byte> key => await BuildAsync(operationId, databaseId, temporary, key, target, cancellationToken).ConfigureAwait(false) switch
            {
                CausalBookmark bookmark => await SwapAsync(operationId, databaseId, temporary, key, bookmark, notifyPeers, cancellationToken).ConfigureAwait(false),
                OperationFailed failed => failed,
            },
            NotFound notFound => new OperationFailed(notFound.Message ?? "database key not found"),
        };
    }

    private Result<Done, Unavailable> RecoverAll(IReadOnlyList<string> stagings)
    {
        foreach (var staging in stagings)
        {
            var recovered = FinishInterruptedSwap(Path.Combine(staging, RewoundDirectoryName)) switch
            {
                Done => ContinuityFiles.DeleteDirectory(staging),
                Unavailable unavailable => unavailable,
            };
            var failure = recovered switch
            {
                Done => null,
                Unavailable unavailable => unavailable,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return ContinuityFiles.DeleteDirectory(_layout.StagingRoot);
    }

    private Result<Done, Unavailable> FinishInterruptedSwap(string rewound) => ContinuityFiles.ListFiles(rewound, "*.db") switch
    {
        IReadOnlyList<string> { Count: 1 } files when Ulid.TryParse(Path.GetFileNameWithoutExtension(files[0]), out var databaseId) && !_layout.DirectoryExists(databaseId) =>
            ContinuityFiles.MoveDirectory(rewound, _layout.DatabaseDirectory(databaseId)),
        IReadOnlyList<string> => new Done(),
        Unavailable unavailable => unavailable,
    };

    private async ValueTask<Result<CausalBookmark, OperationFailed>> BuildAsync(
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

        return await sessions.ResolveKeyAsync(sourceId, cancellationToken).ConfigureAwait(false) switch
        {
            ReadOnlyMemory<byte> sourceKey => await BuildWithKeyAsync(operationId, sourceId, sourceKey, targetId, targetKey, target, cancellationToken).ConfigureAwait(false),
            NotFound notFound => new OperationFailed(notFound.Message ?? "source database key not found"),
        };
    }

    private async ValueTask<Result<CausalBookmark, OperationFailed>> BuildWithKeyAsync(
        Ulid operationId,
        Ulid sourceId,
        ReadOnlyMemory<byte> sourceKey,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        var local = await sessions.OpenAsync(sourceId, sourceKey, cancellationToken).ConfigureAwait(false) switch
        {
            ISqlDatabaseSession session => await BranchLocalAsync(session, targetId, targetKey, target, cancellationToken).ConfigureAwait(false),
            NotFound => Local(new OperationFailed("source database is not stored on this node", ArchiveMayHelp: true)),
            Unavailable unavailable => Local(new OperationFailed(unavailable.Message)),
        };
        return local switch
        {
            CausalBookmark bookmark => bookmark,
            OperationFailed { ArchiveMayHelp: true } failed when manifests.IsEnabled =>
                await BuildFromArchiveAsync(operationId, sourceId, sourceKey, targetId, targetKey, target, cancellationToken).ConfigureAwait(false),
            OperationFailed failed => failed,
        };

        static Result<CausalBookmark, OperationFailed> Local(OperationFailed failed) => failed;
    }

    private async ValueTask<Result<CausalBookmark, OperationFailed>> BranchLocalAsync(
        ISqlDatabaseSession source,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        await using (source.ConfigureAwait(false))
        {
            return await ResolveAsync(source, target, CausalBookmark.Empty, cancellationToken).ConfigureAwait(false) switch
            {
                CausalBookmark at => await BranchAsync(sessions.Engine, source.DatabaseId, targetId, targetKey, at, cancellationToken).ConfigureAwait(false),
                OperationFailed failed => failed,
            };
        }
    }

    private static async ValueTask<Result<CausalBookmark, OperationFailed>> BranchAsync(
        ISqlStorageEngine engine,
        Ulid sourceId,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        CausalBookmark at,
        CancellationToken cancellationToken) =>
        await engine.BranchAsync(new DatabaseBranchRequest(sourceId, targetId, targetKey, at), cancellationToken).ConfigureAwait(false) switch
        {
            ISqlDatabaseSession branch => await CloseBranchAsync(branch).ConfigureAwait(false),
            NotFound notFound => new OperationFailed(notFound.Message ?? "source database is not open", ArchiveMayHelp: true),
            Validation validation => new OperationFailed(validation.Message, ArchiveMayHelp: true),
            Conflict conflict => new OperationFailed(conflict.Message),
            StorageBusy busy => new OperationFailed(busy.Message),
        };

    private static async ValueTask<Result<CausalBookmark, OperationFailed>> CloseBranchAsync(ISqlDatabaseSession branch)
    {
        var bookmark = branch.CurrentBookmark;
        await branch.DisposeAsync().ConfigureAwait(false);
        return bookmark;
    }

    private async ValueTask<Result<CausalBookmark, OperationFailed>> BuildFromArchiveAsync(
        Ulid operationId,
        Ulid sourceId,
        ReadOnlyMemory<byte> sourceKey,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        return await manifests.LoadAsync(sourceId, cancellationToken).ConfigureAwait(false) switch
        {
            ArchiveManifest manifest => await BuildFromManifestAsync(operationId, manifest, sourceId, sourceKey, targetId, targetKey, target, cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => new OperationFailed(unavailable.Message),
        };
    }

    private async ValueTask<Result<CausalBookmark, OperationFailed>> BuildFromManifestAsync(
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
        if (await DownloadAsync(staging, sourceId, manifest, snapshot, target, cancellationToken).ConfigureAwait(false) is { } downloadFailure)
        {
            Cleanup(staging);
            return downloadFailure;
        }

        var staged = await BranchInStagingAsync(staging, sourceId, sourceKey, targetId, targetKey, snapshot.Bookmark, target, cancellationToken).ConfigureAwait(false);
        var placed = staged switch
        {
            CausalBookmark bookmark => Place(ContinuityFiles.MoveDirectory(StorageLayout.DatabaseDirectoryIn(staging, targetId), _layout.DatabaseDirectory(targetId)), bookmark),
            OperationFailed failed => failed,
        };
        Cleanup(staging);
        return placed;

        static Result<CausalBookmark, OperationFailed> Place(Result<Done, Unavailable> moved, CausalBookmark bookmark) => moved switch
        {
            Done => bookmark,
            Unavailable unavailable => new OperationFailed(unavailable.Message),
        };
    }

    private async ValueTask<Result<CausalBookmark, OperationFailed>> BranchInStagingAsync(
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
        await using (engine.ConfigureAwait(false))
        {
            return await engine.OpenAsync(new DatabaseOpenRequest(sourceId, sourceKey), cancellationToken).ConfigureAwait(false) switch
            {
                ISqlDatabaseSession source => await BranchStagedAsync(engine, source, targetId, targetKey, floor, target, cancellationToken).ConfigureAwait(false),
                NotFound notFound => new OperationFailed(notFound.Message ?? "archived snapshot could not be staged"),
                Validation validation => new OperationFailed(validation.Message),
                StorageCorrupted corrupted => new OperationFailed(string.Concat("archived history is corrupted: ", corrupted.Message)),
                StorageBusy busy => new OperationFailed(busy.Message),
            };
        }
    }

    private static async ValueTask<Result<CausalBookmark, OperationFailed>> BranchStagedAsync(
        SqliteStorageEngine engine,
        ISqlDatabaseSession source,
        Ulid targetId,
        ReadOnlyMemory<byte> targetKey,
        CausalBookmark floor,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        await using (source.ConfigureAwait(false))
        {
            var resolved = await ResolveAsync(source, target, floor, cancellationToken).ConfigureAwait(false) switch
            {
                CausalBookmark at => await BranchAsync(engine, source.DatabaseId, targetId, targetKey, at, cancellationToken).ConfigureAwait(false),
                OperationFailed failed => failed,
            };
            return resolved switch
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

        if (Downloaded(await manifests.Store.DownloadToFileAsync(snapshot.Archived.Key, databaseFile, cancellationToken).ConfigureAwait(false)) is { } fetched)
        {
            return fetched;
        }

        if (Local(ContinuityFiles.CopyFile(databaseFile, snapshotFile)) is { } copied)
        {
            return copied;
        }

        foreach (var group in manifest.Segments.GroupBy(static segment => segment.Origin, StringComparer.Ordinal))
        {
            var baseline = snapshot.Bookmark.Get(group.Key);
            var history = Chain([.. group], 1);
            var chain = history.Count > 0 && history[^1].LastSequence >= baseline ? history : Chain([.. group], baseline + 1);
            foreach (var segment in chain)
            {
                if (segment.LastSequence > baseline && !Needed(segment, target))
                {
                    break;
                }

                var path = StorageLayout.SegmentFileIn(staging, sourceId, segment.Origin, segment.FirstSequence, segment.LastSequence);
                if (Downloaded(await manifests.Store.DownloadToFileAsync(segment.Key, path, cancellationToken).ConfigureAwait(false)) is { } failure)
                {
                    return failure;
                }
            }
        }

        return null;
    }

    private async ValueTask<Result<CausalBookmark, OperationFailed>> SwapAsync(
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

        if (await CopyRewoundHistoryAsync(temporary, key, rewound, databaseId, bookmark, cancellationToken).ConfigureAwait(false) is { } historyFailure)
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

        var deleted = await sessions.Engine.DeleteAsync(databaseId, cancellationToken).ConfigureAwait(false) switch
        {
            Done or NotFound => null,
            StorageBusy busy => new OperationFailed(string.Concat("current database could not be closed for the rewind: ", busy.Message)),
        };
        if (deleted is not null)
        {
            return deleted;
        }

        if (Local(ContinuityFiles.MoveDirectory(rewound, _layout.DatabaseDirectory(databaseId))) is { } placed)
        {
            return placed with { Message = string.Concat(placed.Message, "; rewound files remain in ", rewound) };
        }

        Cleanup(staging);
        await sessions.Catalog.DatabaseReplacedAsync(databaseId, notifyPeers ? bookmark : null, cancellationToken).ConfigureAwait(false);
        return bookmark;
    }

    private async ValueTask<OperationFailed?> CopyRewoundHistoryAsync(
        Ulid temporary,
        ReadOnlyMemory<byte> key,
        string destination,
        Ulid databaseId,
        CausalBookmark bookmark,
        CancellationToken cancellationToken) =>
        await sessions.OpenAsync(temporary, key, cancellationToken).ConfigureAwait(false) switch
        {
            ISqlDatabaseSession session => await CopyAndCloseAsync(session, destination, databaseId, key, bookmark).ConfigureAwait(false),
            NotFound => null,
            Unavailable unavailable => new OperationFailed(unavailable.Message),
        };

    private static async ValueTask<OperationFailed?> CopyAndCloseAsync(
        ISqlDatabaseSession session,
        string destination,
        Ulid databaseId,
        ReadOnlyMemory<byte> key,
        CausalBookmark bookmark)
    {
        await using (session.ConfigureAwait(false))
        {
            return SqliteStorageEngine.CopyHistory(session, destination, databaseId, key, bookmark) is { } busy
                ? new OperationFailed(busy.Message)
                : null;
        }
    }

    private static OperationFailed? MoveDatabaseFiles(string directory, string prefix, string destination, string replacement) =>
        ContinuityFiles.ListFiles(directory, string.Concat(prefix, "*")) switch
        {
            IReadOnlyList<string> { Count: 0 } => new OperationFailed("rewound database file is missing"),
            IReadOnlyList<string> files => MoveAll(files, prefix, destination, replacement),
            Unavailable unavailable => new OperationFailed(unavailable.Message),
        };

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

    private static async ValueTask<Result<CausalBookmark, OperationFailed>> ResolveAsync(ISqlDatabaseSession session, RestoreTarget target, CausalBookmark floor, CancellationToken cancellationToken) =>
        target switch
        {
            DateTimeOffset at => await ResolveTimeAsync(session, at, floor, cancellationToken).ConfigureAwait(false),
            CausalBookmark bookmark => bookmark,
        };

    private static async ValueTask<Result<CausalBookmark, OperationFailed>> ResolveTimeAsync(ISqlDatabaseSession session, DateTimeOffset at, CausalBookmark floor, CancellationToken cancellationToken)
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
                scan.Apply(await session.ReadCommitLogAsync(origin, scan.Next, ScanPageSize, cancellationToken).ConfigureAwait(false));
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

    private static OperationFailed? Local(Result<Done, Unavailable> result) => result switch
    {
        Done => null,
        Unavailable unavailable => new OperationFailed(unavailable.Message),
    };

    private static OperationFailed? Downloaded(Result<long, NotFound, Unavailable, RequestTimeout> result) => result switch
    {
        long => null,
        NotFound notFound => new OperationFailed(string.Concat("archived object is missing: ", notFound.Message)),
        Unavailable unavailable => new OperationFailed(unavailable.Message),
        RequestTimeout timeout => new OperationFailed(timeout.Message),
    };

    private void Cleanup(string staging)
    {
        var cleaned = ContinuityFiles.DeleteDirectory(staging) switch
        {
            Done => null,
            Unavailable unavailable => unavailable.Message,
        };
        if (cleaned is not null)
        {
            ContinuityLog.StagingCleanupFailed(logger, cleaned);
        }
    }

    private sealed class TimeScan(string origin, long limit, long next)
    {
        private bool _complete;

        public long Next { get; private set; } = next;

        public long Last { get; private set; }

        public OperationFailed? Failure { get; private set; }

        public bool Continues(long current) => !_complete && Failure is null && Next <= current;

        public void Apply(Result<CommitLogPage, CommitLogGap, StorageBusy> read) => Failure = read switch
        {
            CommitLogPage page => Accept(page),
            CommitLogGap gap => new OperationFailed(string.Concat("commit log for origin ", origin, " no longer holds history before sequence ", gap.OldestAvailable.ToString(CultureInfo.InvariantCulture)), ArchiveMayHelp: true),
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
