namespace TerraBase.Engine.Revisions;

internal enum FullReason
{
    Bootstrap,
    Unrecognized,
    OtherIncarnation,
    Ahead,
    Expired,
    Rewritten,
}

internal readonly record struct RevisionPlan(FullReason? Full, IReadOnlySet<uint> Pages)
{
    public static RevisionPlan Resend(FullReason reason) => new(reason, new HashSet<uint>());
}

internal sealed record RevisionPullResult(DatabaseRevision Revision, FullReason? Full, int Pages, uint DatabaseSize);

internal static class RevisionPlanner
{
    public static RevisionPlan Plan(RevisionManifest manifest, LiveGeneration live, string? client)
    {
        if (string.IsNullOrEmpty(client))
        {
            return RevisionPlan.Resend(FullReason.Bootstrap);
        }

        if (!DatabaseRevision.TryParse(client, out var revision))
        {
            return RevisionPlan.Resend(FullReason.Unrecognized);
        }

        if (revision.Incarnation != manifest.Incarnation)
        {
            return RevisionPlan.Resend(FullReason.OtherIncarnation);
        }

        if (revision.Generation > manifest.Generation)
        {
            return RevisionPlan.Resend(FullReason.Ahead);
        }

        var pages = new HashSet<uint>();
        if (revision.Generation < manifest.Generation)
        {
            if (Older(manifest, revision, pages) is { } reason)
            {
                return RevisionPlan.Resend(reason);
            }
        }
        else if (revision.Frame > live.MaxFrame)
        {
            return RevisionPlan.Resend(FullReason.Ahead);
        }

        if (live.Full)
        {
            return RevisionPlan.Resend(FullReason.Rewritten);
        }

        var floor = revision.Generation == manifest.Generation ? revision.Frame : 0;
        foreach (var (page, last) in live.Pages)
        {
            if (last > floor)
            {
                pages.Add(page);
            }
        }

        return new RevisionPlan(null, pages);
    }

    private static FullReason? Older(RevisionManifest manifest, DatabaseRevision revision, HashSet<uint> pages)
    {
        int start;
        ulong floor;
        ulong expected;
        var exact = manifest.Sealed.FindIndex(sealed => sealed.Generation == revision.Generation);
        if (exact >= 0)
        {
            (start, floor, expected) = (exact, revision.Frame, revision.Generation);
        }
        else
        {
            var first = manifest.Horizon.FindIndex(bucket =>
                bucket.FirstGeneration <= revision.Generation && revision.Generation <= bucket.LastGeneration);
            if (first < 0)
            {
                return FullReason.Expired;
            }

            var offset = (int)(revision.Generation - manifest.Horizon[first].FirstGeneration);
            if (revision.Frame > manifest.Horizon[first].EndFrames[offset])
            {
                return FullReason.Ahead;
            }

            var next = manifest.Horizon[first].FirstGeneration;
            foreach (var bucket in manifest.Horizon.Skip(first))
            {
                if (bucket.FirstGeneration != next)
                {
                    return FullReason.Expired;
                }

                pages.UnionWith(bucket.Pages);
                next = bucket.LastGeneration + 1;
            }

            (start, floor, expected) = (0, 0, next);
        }

        foreach (var sealed in manifest.Sealed.Skip(start))
        {
            if (sealed.Generation != expected)
            {
                return FullReason.Expired;
            }

            if (sealed.Full)
            {
                return FullReason.Rewritten;
            }

            if (floor > sealed.EndFrame)
            {
                return FullReason.Ahead;
            }

            foreach (var (page, last) in sealed.Pages)
            {
                if (last > floor)
                {
                    pages.Add(page);
                }
            }

            floor = 0;
            expected++;
        }

        return expected == manifest.Generation ? null : FullReason.Expired;
    }
}
