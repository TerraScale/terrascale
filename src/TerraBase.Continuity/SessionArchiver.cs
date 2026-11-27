using TerraBase.Storage;

namespace TerraBase.Continuity;

internal sealed record StagedArchiveSnapshot(ArchivedSnapshot Snapshot, string Path, StagedSnapshotUpload Upload);

internal readonly union ArchiveSnapshotOutcome(StagedArchiveSnapshot, ContinuityUnavailable);

internal readonly union ArchivedSegmentOutcome(ArchivedSegment, ContinuityUnavailable);

internal readonly union SegmentCommitTimeOutcome(DateTimeOffset, ContinuityUnavailable);

internal sealed record SnapshotStillDue;

internal sealed record SnapshotTakenElsewhere;

internal readonly union SnapshotRecheckOutcome(SnapshotStillDue, SnapshotTakenElsewhere, ContinuityUnavailable);

internal sealed class SessionArchiver(
    ISqlDatabaseSession session,
    ArchiveManifests manifests,
    ContinuitySessions sessions,
    ContinuityOptions options,
    ContinuityMetrics metrics,
    TimeProvider timeProvider)
{
    private const string LocalSnapshotPattern = "*.db";
    private const string StaleCompactPattern = "*.compact";
    private const string StaleCompactJournalPattern = "*.compact-journal";

    public async ValueTask<ArchiveManifestOutcome> RunAsync(
        ArchiveManifest manifest,
        bool forceSnapshot,
        CancellationToken cancellationToken)
    {
        SqliteStorageEngine.SealCommittedLogs(session);
        var snapshotFloor = ArchiveRetention.SnapshotFloor(manifest);
        List<ArchivedSegment> segments = [];
        ContinuityUnavailable? failure = null;
        foreach (var (origin, _) in session.CurrentBookmark.Entries)
        {
            failure = await ArchiveSegmentsAsync(origin, manifest.ArchivedThrough(origin, snapshotFloor), segments, cancellationToken);
            if (failure is not null)
            {
                break;
            }
        }

        var isPreferred = await sessions.Catalog.IsPreferredArchiverAsync(session.DatabaseId, cancellationToken);
        StagedArchiveSnapshot? snapshot = null;
        if (failure is null && session.CurrentBookmark.Dominates(snapshotFloor) && SnapshotDue(manifest, forceSnapshot, isPreferred))
        {
            SnapshotRecheckOutcome recheck = forceSnapshot ? new SnapshotStillDue() : await RecheckSnapshotAsync(isPreferred, cancellationToken);
            var proceed = recheck switch
            {
                SnapshotStillDue => true,
                SnapshotTakenElsewhere => false,
                ContinuityUnavailable => false,
            };
            failure = recheck switch
            {
                SnapshotStillDue or SnapshotTakenElsewhere => null,
                ContinuityUnavailable unavailable => unavailable,
            };
            if (proceed)
            {
                var staged = await ArchiveSnapshotAsync(cancellationToken);
                (snapshot, failure) = staged switch
                {
                    StagedArchiveSnapshot archived => (archived, (ContinuityUnavailable?)null),
                    ContinuityUnavailable unavailable => ((StagedArchiveSnapshot?)null, unavailable),
                };
            }
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
            && stored.Snapshots.Any(archived => archived.Key == snapshot.Snapshot.Key || archived.Bookmark == snapshot.Snapshot.Bookmark)
            && PruneLocalSnapshots(snapshot.Path) is { } ioFailure)
        {
            return ioFailure;
        }

        return stored;
    }

    private async ValueTask<SnapshotRecheckOutcome> RecheckSnapshotAsync(bool isPreferred, CancellationToken cancellationToken)
    {
        var current = await manifests.ReadCurrentAsync(session.DatabaseId, cancellationToken);
        return current switch
        {
            ArchiveManifest fresh when session.CurrentBookmark.Dominates(ArchiveRetention.SnapshotFloor(fresh))
                && SnapshotDue(fresh, forceSnapshot: false, isPreferred) => new SnapshotStillDue(),
            ArchiveManifest => new SnapshotTakenElsewhere(),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private bool SnapshotDue(ArchiveManifest manifest, bool forceSnapshot, bool isPreferred)
    {
        ArchivedSnapshot? latestArchived = null;
        CausalBookmark? latestBookmark = null;
        for (var index = manifest.Snapshots.Count - 1; index >= 0; index--)
        {
            var candidate = manifest.Snapshots[index];
            if (latestArchived is not null && candidate.TakenAtUtc <= latestArchived.TakenAtUtc)
            {
                continue;
            }

            if (CausalBookmark.TryDecode(candidate.Bookmark, out var bookmark))
            {
                latestArchived = candidate;
                latestBookmark = bookmark;
            }
        }

        if (latestArchived is null || latestBookmark is null)
        {
            return true;
        }

        if (latestBookmark.Equals(session.CurrentBookmark))
        {
            return false;
        }

        var interval = isPreferred ? options.SnapshotInterval : options.SnapshotInterval + options.SnapshotFailoverGrace;
        return forceSnapshot || timeProvider.GetUtcNow() - latestArchived.TakenAtUtc >= interval;
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
        var ordered = OrderSegments(segments);
        for (var index = 0; index < ordered.Count; index++)
        {
            var uploaded = await UploadSegmentAsync(ordered[index], cancellationToken);
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

        static IReadOnlyList<CommitLogSegment> OrderSegments(IReadOnlyList<CommitLogSegment> items)
        {
            if (items.Count <= 1)
            {
                return items;
            }

            for (var index = 1; index < items.Count; index++)
            {
                if (items[index - 1].FirstSequence > items[index].FirstSequence)
                {
                    var sorted = new CommitLogSegment[items.Count];
                    for (var copy = 0; copy < items.Count; copy++)
                    {
                        sorted[copy] = items[copy];
                    }

                    Array.Sort(sorted, static (left, right) => left.FirstSequence.CompareTo(right.FirstSequence));
                    return sorted;
                }
            }

            return items;
        }

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
        if (segment.FirstCommittedAtUtc is { } knownFirstAt && segment.LastCommittedAtUtc is { } knownLastAt)
        {
            return new ArchivedSegment(
                key,
                segment.OriginRegion,
                segment.FirstSequence,
                segment.LastSequence,
                segment.SizeBytes,
                segment.SealedAtUtc,
                knownFirstAt,
                knownLastAt);
        }

        var first = segment.FirstCommittedAtUtc is { } firstCommitted
            ? (SegmentCommitTimeOutcome)firstCommitted
            : await CommittedAtAsync(segment.OriginRegion, segment.FirstSequence, cancellationToken);
        var last = segment.LastCommittedAtUtc is { } lastCommitted
            ? (SegmentCommitTimeOutcome)lastCommitted
            : await CommittedAtAsync(segment.OriginRegion, segment.LastSequence, cancellationToken);
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
            CommitLogPage { Entries: [var entry, ..] } when entry.Sequence == sequence => entry.Hlc.WallTime,
            CommitLogPage => new ContinuityUnavailable("archived segment entry is not readable from the local commit log", "segment_unreadable"),
            CommitLogGap gap => new ContinuityUnavailable(string.Concat("local commit log no longer holds origin ", gap.OriginRegion, " entries for the archived segment"), "segment_unreadable"),
            StorageCorrupted corrupted => new ContinuityUnavailable(corrupted.Message, "storage_corrupted"),
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
        var failure = listed switch
        {
            IReadOnlyList<string> files => DeleteFiles(files.OrderByDescending(static file => Path.GetFileName(file), StringComparer.Ordinal).Skip(options.LocalSnapshotsRetained)),
            ContinuityIoFailure io => new ContinuityUnavailable(io.Message, io.Code),
        };
        return failure ?? PruneStale(directory, StaleCompactPattern) ?? PruneStale(directory, StaleCompactJournalPattern);
    }

    private static ContinuityUnavailable? PruneStale(string directory, string pattern)
    {
        var listed = ContinuityFiles.ListFiles(directory, pattern);
        return listed switch
        {
            IReadOnlyList<string> files => DeleteFiles(files),
            ContinuityIoFailure io => new ContinuityUnavailable(io.Message, io.Code),
        };
    }

    private static ContinuityUnavailable? DeleteFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
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

    private static ContinuityUnavailable Busy(StorageBusy busy) => new(busy.Message, "storage_busy");
}
