namespace TerraBase.Continuity;

internal sealed record RetainedArchive(ArchiveManifest Manifest, IReadOnlyList<string> RemovedKeys);

internal static class ArchiveRetention
{
    public static CausalBookmark SnapshotFloor(ArchiveManifest manifest)
    {
        var snapshots = ManifestCodec.Snapshots(manifest);
        if (snapshots is not [var first, ..])
        {
            return CausalBookmark.Empty;
        }

        Dictionary<string, long> floor = new(first.Bookmark.Entries, StringComparer.Ordinal);
        foreach (var snapshot in snapshots.Skip(1))
        {
            foreach (var (origin, sequence) in floor)
            {
                floor[origin] = Math.Min(sequence, snapshot.Bookmark.Get(origin));
            }
        }

        return CausalBookmark.Create(floor);
    }

    public static RetainedArchive Select(ArchiveManifest manifest, DateTimeOffset cutoff)
    {
        var anchor = manifest.Snapshots.Where(snapshot => snapshot.TakenAtUtc <= cutoff).MaxBy(static snapshot => snapshot.TakenAtUtc);
        if (anchor is null)
        {
            return new RetainedArchive(manifest, []);
        }

        List<string> removed = [];
        List<ArchivedSnapshot> snapshots = [];
        foreach (var snapshot in manifest.Snapshots)
        {
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
        List<ArchivedSegment> segments = [];
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
