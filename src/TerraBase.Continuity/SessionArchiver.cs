using TerraBase.Storage;

namespace TerraBase.Continuity;

internal sealed record ArchiveStep(ArchiveManifest Manifest, Unavailable? Failure);

internal sealed class SessionArchiver(
    ISqlDatabaseSession session,
    ArchiveManifests manifests,
    ISqlStorageEngine engine,
    ContinuityOptions options,
    ContinuityMetrics metrics,
    TimeProvider timeProvider)
{
    private const string LocalSnapshotPattern = "*.db";

    public async ValueTask<Result<ArchiveManifest, Unavailable>> RunAsync(ArchiveManifest manifest, bool forceSnapshot, CancellationToken cancellationToken)
    {
        SqliteStorageEngine.SealCommittedLogs(session);
        var step = new ArchiveStep(manifest, null);
        foreach (var (origin, _) in session.CurrentBookmark.Entries)
        {
            step = await ArchiveSegmentsAsync(step.Manifest, origin, cancellationToken).ConfigureAwait(false);
            if (step.Failure is not null)
            {
                break;
            }
        }

        if (step.Failure is null && SnapshotDue(step.Manifest, forceSnapshot))
        {
            step = await ArchiveSnapshotAsync(step.Manifest, cancellationToken).ConfigureAwait(false);
        }

        if (ReferenceEquals(step.Manifest, manifest))
        {
            return Finish(step);
        }

        return await manifests.SaveAsync(step.Manifest, cancellationToken).ConfigureAwait(false) switch
        {
            ArchiveManifest saved => Finish(step with { Manifest = saved }),
            Unavailable unavailable => step.Failure ?? unavailable,
        };
    }

    private static Result<ArchiveManifest, Unavailable> Finish(ArchiveStep step) =>
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

    private async ValueTask<ArchiveStep> ArchiveSegmentsAsync(ArchiveManifest manifest, string origin, CancellationToken cancellationToken) =>
        await session.ListSealedSegmentsAsync(origin, manifest.ArchivedSegmentThrough(origin), cancellationToken).ConfigureAwait(false) switch
        {
            IReadOnlyList<CommitLogSegment> segments => await UploadSegmentsAsync(manifest, segments, cancellationToken).ConfigureAwait(false),
            StorageBusy busy => new ArchiveStep(manifest, Busy(busy)),
        };

    private async ValueTask<ArchiveStep> UploadSegmentsAsync(ArchiveManifest manifest, IReadOnlyList<CommitLogSegment> segments, CancellationToken cancellationToken)
    {
        var current = manifest;
        foreach (var segment in segments.OrderBy(static segment => segment.FirstSequence))
        {
            var step = await UploadSegmentAsync(segment, cancellationToken).ConfigureAwait(false) switch
            {
                ArchivedSegment archived => new ArchiveStep(current with { Segments = [.. current.Segments.Where(existing => existing.Key != archived.Key), archived] }, null),
                Unavailable unavailable => new ArchiveStep(current, unavailable),
            };
            if (step.Failure is not null)
            {
                return step;
            }

            current = step.Manifest;
        }

        return new ArchiveStep(current, null);
    }

    private async ValueTask<Result<ArchivedSegment, Unavailable>> UploadSegmentAsync(CommitLogSegment segment, CancellationToken cancellationToken) =>
        await session.OpenSegmentAsync(segment, cancellationToken).ConfigureAwait(false) switch
        {
            Stream content => await UploadOpenSegmentAsync(segment, content, cancellationToken).ConfigureAwait(false),
            NotFound notFound => new Unavailable(notFound.Message ?? "sealed segment disappeared before it was archived", "segment_missing"),
            StorageBusy busy => Busy(busy),
        };

    private async ValueTask<Result<ArchivedSegment, Unavailable>> UploadOpenSegmentAsync(CommitLogSegment segment, Stream content, CancellationToken cancellationToken)
    {
        var key = ArchiveKeys.Segment(session.DatabaseId, segment);
        await using (content.ConfigureAwait(false))
        {
            var uploaded = await manifests.Store.UploadStreamAsync(key, content, ManifestCodec.BinaryContentType, cancellationToken).ConfigureAwait(false);
            var failure = uploaded switch
            {
                Done => null,
                Unavailable unavailable => unavailable,
                RequestTimeout timeout => new Unavailable(timeout.Message, timeout.Code),
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        metrics.Uploaded(ArchiveObjectKind.Segment, segment.SizeBytes);
        var first = await CommittedAtAsync(segment.OriginRegion, segment.FirstSequence, cancellationToken).ConfigureAwait(false);
        var last = await CommittedAtAsync(segment.OriginRegion, segment.LastSequence, cancellationToken).ConfigureAwait(false);
        return (first, last) switch
        {
            (DateTimeOffset firstAt, DateTimeOffset lastAt) => new ArchivedSegment(
                key,
                segment.OriginRegion,
                segment.FirstSequence,
                segment.LastSequence,
                segment.SizeBytes,
                segment.SealedAtUtc,
                firstAt,
                lastAt),
            (Unavailable unavailable, _) => unavailable,
            (_, Unavailable unavailable) => unavailable,
        };
    }

    private async ValueTask<Result<DateTimeOffset, Unavailable>> CommittedAtAsync(string origin, long sequence, CancellationToken cancellationToken) =>
        await session.ReadCommitLogAsync(origin, sequence, 1, cancellationToken).ConfigureAwait(false) switch
        {
            CommitLogPage { Entries: [var entry, ..] } when entry.Sequence == sequence => DateTimeOffset.FromUnixTimeMilliseconds(entry.Hlc.WallMs),
            CommitLogPage => new Unavailable("archived segment entry is not readable from the local commit log", "segment_unreadable"),
            CommitLogGap gap => new Unavailable(string.Concat("local commit log no longer holds origin ", gap.OriginRegion, " entries for the archived segment"), "segment_unreadable"),
            StorageBusy busy => Busy(busy),
        };

    private async ValueTask<ArchiveStep> ArchiveSnapshotAsync(ArchiveManifest manifest, CancellationToken cancellationToken) =>
        await engine.SnapshotAsync(session.DatabaseId, cancellationToken).ConfigureAwait(false) switch
        {
            DatabaseSnapshot snapshot => await UploadSnapshotAsync(manifest, snapshot, cancellationToken).ConfigureAwait(false),
            NotFound notFound => new ArchiveStep(manifest, new Unavailable(notFound.Message ?? "database closed before it could be snapshotted", "snapshot_unavailable")),
            StorageBusy busy => new ArchiveStep(manifest, Busy(busy)),
        };

    private async ValueTask<ArchiveStep> UploadSnapshotAsync(ArchiveManifest manifest, DatabaseSnapshot snapshot, CancellationToken cancellationToken)
    {
        var key = ArchiveKeys.Snapshot(session.DatabaseId, snapshot.Bookmark);
        var uploaded = await manifests.Store.UploadFileAsync(key, snapshot.Path, cancellationToken).ConfigureAwait(false);
        var failure = uploaded switch
        {
            Done => PruneLocalSnapshots(snapshot.Path),
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => new Unavailable(timeout.Message, timeout.Code),
        };
        if (failure is not null)
        {
            return new ArchiveStep(manifest, failure);
        }

        metrics.Uploaded(ArchiveObjectKind.Snapshot, snapshot.SizeBytes);
        var archived = new ArchivedSnapshot(key, snapshot.Bookmark.Encode(), snapshot.SchemaEpoch, snapshot.SizeBytes, snapshot.TakenAtUtc);
        return new ArchiveStep(manifest with { Snapshots = [.. manifest.Snapshots.Where(existing => existing.Key != key), archived] }, null);
    }

    private Unavailable? PruneLocalSnapshots(string snapshotPath)
    {
        if (Path.GetDirectoryName(snapshotPath) is not { } directory)
        {
            return null;
        }

        return ContinuityFiles.ListFiles(directory, LocalSnapshotPattern) switch
        {
            IReadOnlyList<string> files => DeleteOldest(files, options.LocalSnapshotsRetained),
            Unavailable unavailable => unavailable,
        };

        static Unavailable? DeleteOldest(IReadOnlyList<string> files, int retained)
        {
            foreach (var file in files.OrderByDescending(static file => Path.GetFileName(file), StringComparer.Ordinal).Skip(retained))
            {
                var deleted = ContinuityFiles.DeleteFile(file) switch
                {
                    Done => null,
                    Unavailable unavailable => unavailable,
                };
                if (deleted is not null)
                {
                    return deleted;
                }
            }

            return null;
        }
    }

    private static Unavailable Busy(StorageBusy busy) => new(busy.Message, "storage_busy");
}
