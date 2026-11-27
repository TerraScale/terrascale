namespace TerraBase.Continuity;

internal sealed record RetainedArchive(ArchiveManifest Manifest, IReadOnlyList<string> RemovedKeys);

internal static class ArchiveRetention
{
    public static CausalBookmark SnapshotFloor(ArchiveManifest manifest)
    {
        var count = manifest.Snapshots.Count;
        if (count == 0)
        {
            return CausalBookmark.Empty;
        }

        if (count == 1)
        {
            return CausalBookmark.TryDecode(manifest.Snapshots[0].Bookmark, out var single) ? single : CausalBookmark.Empty;
        }

        CausalBookmark? first = null;
        Dictionary<string, long>? floor = null;
        for (var index = 0; index < count; index++)
        {
            if (!CausalBookmark.TryDecode(manifest.Snapshots[index].Bookmark, out var bookmark))
            {
                continue;
            }

            if (first is null)
            {
                first = bookmark;
                continue;
            }

            floor ??= new Dictionary<string, long>(first.Entries, StringComparer.Ordinal);
            foreach (var (origin, sequence) in floor)
            {
                var candidate = bookmark.Get(origin);
                if (candidate < sequence)
                {
                    floor[origin] = candidate;
                }
            }
        }

        if (floor is not null)
        {
            return CausalBookmark.Create(floor);
        }

        return first ?? CausalBookmark.Empty;
    }

    public static RetainedArchive Select(ArchiveManifest manifest, DateTimeOffset cutoff)
    {
        ArchivedSnapshot? anchor = null;
        for (var index = 0; index < manifest.Snapshots.Count; index++)
        {
            var candidate = manifest.Snapshots[index];
            if (candidate.TakenAtUtc <= cutoff && (anchor is null || candidate.TakenAtUtc > anchor.TakenAtUtc))
            {
                anchor = candidate;
            }
        }

        if (anchor is null)
        {
            return new RetainedArchive(manifest, []);
        }

        List<string> removed = [];
        List<ArchivedSnapshot> snapshots = new(manifest.Snapshots.Count);
        for (var index = 0; index < manifest.Snapshots.Count; index++)
        {
            var snapshot = manifest.Snapshots[index];
            if (snapshot.TakenAtUtc < anchor.TakenAtUtc)
            {
                removed.Add(snapshot.Key);
            }
            else
            {
                snapshots.Add(snapshot);
            }
        }

        var retained = manifest with { Snapshots = snapshots };
        var floor = SnapshotFloor(retained);
        List<ArchivedSegment> segments = new(manifest.Segments.Count);
        foreach (var origin in manifest.Segments.GroupBy(static segment => segment.Origin, StringComparer.Ordinal))
        {
            var covered = floor.Get(origin.Key);
            var keep = false;
            foreach (var segment in origin.OrderBy(static segment => segment.FirstSequence))
            {
                keep |= segment.LastSequence > covered || segment.SealedAtUtc >= cutoff || segment.LastCommittedAtUtc >= cutoff;
                if (keep)
                {
                    segments.Add(segment);
                }
                else
                {
                    removed.Add(segment.Key);
                }
            }
        }

        return removed.Count == 0 ? new RetainedArchive(manifest, []) : new RetainedArchive(retained with { Segments = segments }, removed);
    }
}
