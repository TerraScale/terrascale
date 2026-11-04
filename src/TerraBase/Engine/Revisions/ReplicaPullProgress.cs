namespace TerraBase.Engine.Revisions;

internal sealed class ReplicaPullProgress
{
    private const int MaxCommits = 256;
    private readonly Queue<(DatabaseRevision Revision, DateTimeOffset At)> _commits = new();
    private readonly Dictionary<string, Pull> _pulls = new(StringComparer.Ordinal);
    private DatabaseRevision? _baseline;

    private sealed record Pull(DatabaseRevision Revision, long CdcPosition, DateTimeOffset At, bool Known, DateTimeOffset? MissingSince);

    internal long Bytes => _commits.Count * 64L + _pulls.Count * 256L;

    internal void Reset(DatabaseRevision revision)
    {
        _commits.Clear();
        _pulls.Clear();
        _baseline = revision;
    }

    internal void Committed(DatabaseRevision revision, DateTimeOffset at)
    {
        if (_baseline is null || _baseline.Value.Incarnation != revision.Incarnation)
        {
            Reset(revision);
            return;
        }

        foreach (var (region, pull) in _pulls.ToArray())
        {
            if (pull.Known && pull.MissingSince is null)
            {
                _pulls[region] = pull with { MissingSince = at };
            }
        }

        _commits.Enqueue((revision, at));
        if (_commits.Count > MaxCommits)
        {
            _baseline = _commits.Dequeue().Revision;
        }
    }

    internal void Pulled(string region, DatabaseRevision revision, long cdcPosition, DateTimeOffset at)
    {
        _pulls.TryGetValue(region, out var previous);
        if (previous is not null && previous.Revision.Incarnation == revision.Incarnation && Compare(previous.Revision, revision) > 0)
        {
            _pulls[region] = previous with { At = at };
            return;
        }

        if (previous is null && _pulls.Count >= 8)
        {
            return;
        }

        var known = _baseline is { } baseline && revision.Incarnation == baseline.Incarnation && Compare(revision, baseline) >= 0;
        var missing = known ? _commits.FirstOrDefault(commit => Compare(commit.Revision, revision) > 0).At : (DateTimeOffset?)null;
        if (!known && previous is { Known: true } && previous.Revision == revision)
        {
            known = true;
            missing = previous.MissingSince;
        }

        _pulls[region] = new Pull(revision, cdcPosition, at, known, missing == default(DateTimeOffset) ? null : missing);
    }

    internal long? AcknowledgedCdc(string region, string? revision) =>
        _pulls.TryGetValue(region, out var pull) && pull.Revision.ToString() == revision ? pull.CdcPosition : null;

    internal IReadOnlyList<ReplicaPullObservation> Read(DatabaseRevision current, IReadOnlyList<string> regions, DateTimeOffset now)
    {
        foreach (var removed in _pulls.Keys.Where(region => !regions.Contains(region)).ToArray())
        {
            _pulls.Remove(removed);
        }

        return [.. regions.Select(region =>
        {
            if (!_pulls.TryGetValue(region, out var pull))
            {
                return new ReplicaPullObservation(region, null, null, null);
            }

            double? lag = pull.Known && pull.Revision.Incarnation == current.Incarnation
                ? pull.MissingSince is { } missing ? Math.Max(0, (now - missing).TotalMilliseconds) : 0
                : null;
            return new ReplicaPullObservation(region, lag, pull.At, pull.Revision.ToString());
        })];
    }

    private static int Compare(DatabaseRevision left, DatabaseRevision right) => left.Generation != right.Generation
        ? left.Generation.CompareTo(right.Generation) : left.Frame.CompareTo(right.Frame);
}
