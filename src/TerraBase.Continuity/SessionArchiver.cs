using TerraBase.Storage;

namespace TerraBase.Continuity;

internal sealed record StagedArchiveSnapshot(ArchivedSnapshot Snapshot, string Path, StagedSnapshotUpload Upload);

internal readonly union ArchiveSnapshotOutcome(StagedArchiveSnapshot, ContinuityUnavailable);

internal readonly union ArchivedSegmentOutcome(ArchivedSegment, ContinuityUnavailable);

internal readonly union SegmentCommitTimeOutcome(DateTimeOffset, ContinuityUnavailable);

internal sealed class SessionArchiver(
    ISqlDatabaseSession session,
    ArchiveManifests manifests,
    ContinuitySessions sessions,
    ContinuityOptions options,
    ContinuityMetrics metrics,
    TimeProvider timeProvider)
{
    private const string LocalSnapshotPattern = "*.db";

    public async ValueTask<ArchiveManifestOutcome> RunAsync(
        ArchiveManifest manifest,
        bool forceSnapshot,
        CancellationToken cancellationToken)
    {
        SqliteStorageEngine.SealCommittedLogs(session);
        var snapshotFloor = ArchiveRetention.SnapshotFloor(manifest);
        var archivedThrough = manifest.ArchivedThrough(snapshotFloor);
        List<ArchivedSegment> segments = [];
        ContinuityUnavailable? failure = null;
        foreach (var (origin, _) in session.CurrentBookmark.Entries)
        {
            failure = await ArchiveSegmentsAsync(origin, archivedThrough.GetValueOrDefault(origin), segments, cancellationToken);
            if (failure is not null)
            {
                break;
            }
        }

        StagedArchiveSnapshot? snapshot = null;
        if (failure is null && session.CurrentBookmark.Dominates(snapshotFloor) && SnapshotDue(manifest, forceSnapshot))
        {
            var staged = await ArchiveSnapshotAsync(cancellationToken);
            (snapshot, failure) = staged switch
            {
                StagedArchiveSnapshot archived => (archived, (ContinuityUnavailable?)null),
                ContinuityUnavailable unavailable => ((StagedArchiveSnapshot?)null, unavailable),
            };
        }

        var cutoff = await sessions.ReadRetentionCutoffAsync(session.DatabaseId, cancellationToken);
        var saved = await manifests.PublishAsync(session.DatabaseId, segments, snapshot, cutoff, cancellationToken);
        return saved switch
        {
            ArchiveManifest stored => await FinishAsync(stored, snapshot, cutoff, failure, cancellationToken),
            ContinuityUnavailable unavailable => failure ?? unavailable,
        };
    }

    private async ValueTask<ArchiveManifestOutcome> FinishAsync(
        ArchiveManifest stored,
        StagedArchiveSnapshot? snapshot,
        DateTimeOffset? cutoff,
        ContinuityUnavailable? failure,
        CancellationToken cancellationToken)
    {
        if (failure is not null)
        {
            return failure;
        }

        if (cutoff is { } before)
        {
            var floor = ArchiveRetention.SnapshotFloor(stored);
            var prunable = SqliteCommitLog.PrunableThrough(session, before, floor);
            if (!prunable.IsEmpty)
            {
                var confirmed = await sessions.ConfirmPrunableThroughAsync(session.DatabaseId, session.CurrentBookmark, prunable, cancellationToken);
                if (SqliteCommitLog.PruneArchivedSegments(session, before, prunable, confirmed) is { } busy)
                {
                    return Busy(busy);
                }
            }
        }

        if (snapshot is not null
            && stored.Snapshots.Any(archived => archived.Key == snapshot.Snapshot.Key)
            && PruneLocalSnapshots(snapshot.Path) is { } ioFailure)
        {
            return ioFailure;
        }

        return stored;
    }

    private bool SnapshotDue(ArchiveManifest manifest, bool forceSnapshot)
    {
        if (ManifestCodec.Snapshots(manifest).MaxBy(static snapshot => snapshot.Archived.TakenAtUtc) is not { } latest)
        {
            return true;
        }

        if (latest.Bookmark.Equals(session.CurrentBookmark))
        {
            return false;
        }

        return forceSnapshot || timeProvider.GetUtcNow() - latest.Archived.TakenAtUtc >= options.SnapshotInterval;
    }

    private async ValueTask<ContinuityUnavailable?> ArchiveSegmentsAsync(
        string origin,
        long archivedThrough,
        List<ArchivedSegment> staged,
        CancellationToken cancellationToken)
    {
        var listed = await session.ListSealedSegmentsAsync(origin, archivedThrough, cancellationToken);
        return listed switch
        {
            IReadOnlyList<CommitLogSegment> segments => await UploadSegmentsAsync(segments, staged, cancellationToken),
            StorageBusy busy => Busy(busy),
        };
    }

    private async ValueTask<ContinuityUnavailable?> UploadSegmentsAsync(
        IReadOnlyList<CommitLogSegment> segments,
        List<ArchivedSegment> staged,
        CancellationToken cancellationToken)
    {
        foreach (var segment in segments.OrderBy(static segment => segment.FirstSequence))
        {
            var uploaded = await UploadSegmentAsync(segment, cancellationToken);
            var failure = uploaded switch
            {
                ArchivedSegment archived => Stage(staged, archived),
                ContinuityUnavailable unavailable => unavailable,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;

        static ContinuityUnavailable? Stage(List<ArchivedSegment> staged, ArchivedSegment segment)
        {
            staged.Add(segment);
            return null;
        }
    }

    private async ValueTask<ArchivedSegmentOutcome> UploadSegmentAsync(CommitLogSegment segment, CancellationToken cancellationToken)
    {
        var opened = await session.OpenSegmentAsync(segment, cancellationToken);
        return opened switch
        {
            Stream content => await UploadOpenSegmentAsync(segment, content, cancellationToken),
            SegmentNotFound missing => new ContinuityUnavailable(missing.Message, "segment_missing"),
            StorageBusy busy => Busy(busy),
        };
    }

    private async ValueTask<ArchivedSegmentOutcome> UploadOpenSegmentAsync(CommitLogSegment segment, Stream content, CancellationToken cancellationToken)
    {
        var key = ArchiveKeys.Segment(session.DatabaseId, segment);
        await using (content)
        {
            var uploaded = await manifests.Store.UploadStreamAsync(key, content, ManifestCodec.BinaryContentType, cancellationToken);
            var failure = uploaded switch
            {
                ArchiveObjectWritten => (ContinuityUnavailable?)null,
                ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
                ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        metrics.Uploaded(ArchiveObjectKind.Segment, segment.SizeBytes);
        var first = await CommittedAtAsync(segment.OriginRegion, segment.FirstSequence, cancellationToken);
        var last = await CommittedAtAsync(segment.OriginRegion, segment.LastSequence, cancellationToken);
        return first switch
        {
            ContinuityUnavailable unavailable => unavailable,
            DateTimeOffset firstAt => last switch
            {
                ContinuityUnavailable unavailable => unavailable,
                DateTimeOffset lastAt => new ArchivedSegment(
                    key,
                    segment.OriginRegion,
                    segment.FirstSequence,
                    segment.LastSequence,
                    segment.SizeBytes,
                    segment.SealedAtUtc,
                    firstAt,
                    lastAt),
            },
        };
    }

    private async ValueTask<SegmentCommitTimeOutcome> CommittedAtAsync(string origin, long sequence, CancellationToken cancellationToken)
    {
        var read = await session.ReadCommitLogAsync(origin, sequence, 1, cancellationToken);
        return read switch
        {
            CommitLogPage { Entries: [var entry, ..] } when entry.Sequence == sequence => DateTimeOffset.FromUnixTimeMilliseconds(entry.Hlc.WallMs),
            CommitLogPage => new ContinuityUnavailable("archived segment entry is not readable from the local commit log", "segment_unreadable"),
            CommitLogGap gap => new ContinuityUnavailable(string.Concat("local commit log no longer holds origin ", gap.OriginRegion, " entries for the archived segment"), "segment_unreadable"),
            StorageBusy busy => Busy(busy),
        };
    }

    private async ValueTask<ArchiveSnapshotOutcome> ArchiveSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapped = await sessions.Engine.SnapshotAsync(session.DatabaseId, cancellationToken);
        return snapped switch
        {
            DatabaseSnapshot snapshot => await UploadSnapshotAsync(snapshot, cancellationToken),
            DatabaseNotFound missing => new ContinuityUnavailable(missing.Message, "snapshot_unavailable"),
            StorageBusy busy => Busy(busy),
        };
    }

    private async ValueTask<ArchiveSnapshotOutcome> UploadSnapshotAsync(DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        var key = ArchiveKeys.Snapshot(session.DatabaseId, snapshot.TakenAtUtc, snapshot.Bookmark);
        var staged = await manifests.StageSnapshotUploadAsync(session.DatabaseId, key, cancellationToken);
        return staged switch
        {
            StagedSnapshotUpload upload when timeProvider.GetUtcNow() < upload.DeadlineUtc =>
                await UploadSnapshotAsync(snapshot, key, upload, cancellationToken),
            StagedSnapshotUpload => new ContinuityUnavailable("snapshot upload reservation expired", "snapshot_upload_expired"),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ArchiveSnapshotOutcome> UploadSnapshotAsync(
        DatabaseSnapshot snapshot, string key, StagedSnapshotUpload upload, CancellationToken cancellationToken)
    {
        var uploaded = await manifests.Store.UploadFileAsync(key, snapshot.Path, cancellationToken);
        var failure = uploaded switch
        {
            ArchiveObjectWritten => (ContinuityUnavailable?)null,
            ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
            ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
        };
        if (failure is not null)
        {
            return failure;
        }

        metrics.Uploaded(ArchiveObjectKind.Snapshot, snapshot.SizeBytes);
        var archived = new ArchivedSnapshot(key, snapshot.Bookmark.Encode(), snapshot.SchemaEpoch, snapshot.SizeBytes, snapshot.TakenAtUtc);
        return new StagedArchiveSnapshot(archived, snapshot.Path, upload);
    }

    private ContinuityUnavailable? PruneLocalSnapshots(string snapshotPath)
    {
        if (Path.GetDirectoryName(snapshotPath) is not { } directory)
        {
            return null;
        }

        var listed = ContinuityFiles.ListFiles(directory, LocalSnapshotPattern);
        return listed switch
        {
            IReadOnlyList<string> files => DeleteOldest(files, options.LocalSnapshotsRetained),
            ContinuityIoFailure failure => new ContinuityUnavailable(failure.Message, failure.Code),
        };

        static ContinuityUnavailable? DeleteOldest(IReadOnlyList<string> files, int retained)
        {
            foreach (var file in files.OrderByDescending(static file => Path.GetFileName(file), StringComparer.Ordinal).Skip(retained))
            {
                var deleted = ContinuityFiles.DeleteFile(file);
                var failure = deleted switch
                {
                    ContinuityFilesChanged => (ContinuityUnavailable?)null,
                    ContinuityIoFailure io => new ContinuityUnavailable(io.Message, io.Code),
                };
                if (failure is not null)
                {
                    return failure;
                }
            }

            return null;
        }
    }

    private static ContinuityUnavailable Busy(StorageBusy busy) => new(busy.Message, "storage_busy");
}
