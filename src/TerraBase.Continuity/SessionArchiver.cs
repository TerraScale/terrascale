using TerraBase.Storage;

namespace TerraBase.Continuity;

internal sealed record ArchiveStep(ArchiveManifest Manifest, ContinuityUnavailable? Failure);

internal readonly union ArchivedSegmentOutcome(ArchivedSegment, ContinuityUnavailable);

internal readonly union SegmentCommitTimeOutcome(DateTimeOffset, ContinuityUnavailable);

internal sealed class SessionArchiver(
    ISqlDatabaseSession session,
    ArchiveManifests manifests,
    ISqlStorageEngine engine,
    ContinuityOptions options,
    ContinuityMetrics metrics,
    TimeProvider timeProvider)
{
    private const string LocalSnapshotPattern = "*.db";

    public async ValueTask<ArchiveManifestOutcome> RunAsync(ArchiveManifest manifest, bool forceSnapshot, CancellationToken cancellationToken)
    {
        SqliteStorageEngine.SealCommittedLogs(session);
        var step = new ArchiveStep(manifest, null);
        foreach (var (origin, _) in session.CurrentBookmark.Entries)
        {
            step = await ArchiveSegmentsAsync(step.Manifest, origin, cancellationToken);
            if (step.Failure is not null)
            {
                break;
            }
        }

        if (step.Failure is null && SnapshotDue(step.Manifest, forceSnapshot))
        {
            step = await ArchiveSnapshotAsync(step.Manifest, cancellationToken);
        }

        if (ReferenceEquals(step.Manifest, manifest))
        {
            return Finish(step);
        }

        var saved = await manifests.SaveAsync(step.Manifest, cancellationToken);
        return saved switch
        {
            ArchiveManifest stored => Finish(step with { Manifest = stored }),
            ContinuityUnavailable unavailable => step.Failure ?? unavailable,
        };
    }

    private static ArchiveManifestOutcome Finish(ArchiveStep step) =>
        step.Failure is { } failure ? failure : step.Manifest;

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

    private async ValueTask<ArchiveStep> ArchiveSegmentsAsync(ArchiveManifest manifest, string origin, CancellationToken cancellationToken)
    {
        var listed = await session.ListSealedSegmentsAsync(origin, manifest.ArchivedSegmentThrough(origin), cancellationToken);
        return listed switch
        {
            IReadOnlyList<CommitLogSegment> segments => await UploadSegmentsAsync(manifest, segments, cancellationToken),
            StorageBusy busy => new ArchiveStep(manifest, Busy(busy)),
        };
    }

    private async ValueTask<ArchiveStep> UploadSegmentsAsync(ArchiveManifest manifest, IReadOnlyList<CommitLogSegment> segments, CancellationToken cancellationToken)
    {
        var current = manifest;
        foreach (var segment in segments.OrderBy(static segment => segment.FirstSequence))
        {
            var uploaded = await UploadSegmentAsync(segment, cancellationToken);
            var step = uploaded switch
            {
                ArchivedSegment archived => new ArchiveStep(current with { Segments = [.. current.Segments.Where(existing => existing.Key != archived.Key), archived] }, null),
                ContinuityUnavailable unavailable => new ArchiveStep(current, unavailable),
            };
            if (step.Failure is not null)
            {
                return step;
            }

            current = step.Manifest;
        }

        return new ArchiveStep(current, null);
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

    private async ValueTask<ArchiveStep> ArchiveSnapshotAsync(ArchiveManifest manifest, CancellationToken cancellationToken)
    {
        var snapped = await engine.SnapshotAsync(session.DatabaseId, cancellationToken);
        return snapped switch
        {
            DatabaseSnapshot snapshot => await UploadSnapshotAsync(manifest, snapshot, cancellationToken),
            DatabaseNotFound missing => new ArchiveStep(manifest, new ContinuityUnavailable(missing.Message, "snapshot_unavailable")),
            StorageBusy busy => new ArchiveStep(manifest, Busy(busy)),
        };
    }

    private async ValueTask<ArchiveStep> UploadSnapshotAsync(ArchiveManifest manifest, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        var key = ArchiveKeys.Snapshot(session.DatabaseId, snapshot.Bookmark);
        var uploaded = await manifests.Store.UploadFileAsync(key, snapshot.Path, cancellationToken);
        var failure = uploaded switch
        {
            ArchiveObjectWritten => PruneLocalSnapshots(snapshot.Path),
            ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
            ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
        };
        if (failure is not null)
        {
            return new ArchiveStep(manifest, failure);
        }

        metrics.Uploaded(ArchiveObjectKind.Snapshot, snapshot.SizeBytes);
        var archived = new ArchivedSnapshot(key, snapshot.Bookmark.Encode(), snapshot.SchemaEpoch, snapshot.SizeBytes, snapshot.TakenAtUtc);
        return new ArchiveStep(manifest with { Snapshots = [.. manifest.Snapshots.Where(existing => existing.Key != key), archived] }, null);
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
