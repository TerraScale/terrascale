using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace TerraBase.Replication;

/// <summary>A half-open sequence range owned by one replication origin.</summary>
public readonly record struct ReplicationRange
{
    public ReplicationRange(string origin, ulong start, ulong end)
    {
        if (string.IsNullOrWhiteSpace(origin)) throw new ArgumentException("An origin is required.", nameof(origin));
        if (start == 0 || end < start) throw new ArgumentOutOfRangeException(nameof(start), "Ranges are one-based and inclusive.");
        Origin = origin;
        Start = start;
        End = end;
    }

    public string Origin { get; }

    public ulong Start { get; }

    public ulong End { get; }

    public ulong Count => End - Start + 1;

    public override string ToString() => $"{Origin}:{Start}-{End}";
}

/// <summary>A deterministic additive summary for one journal range.</summary>
public sealed record RangeFingerprint(ReplicationRange Range, ulong PresentCount, string Digest)
{
    public bool IsEmpty => PresentCount == 0;
}

/// <summary>
/// An append-only per-origin publication journal. Entries may arrive out of
/// order, but an origin's frontier advances only over a contiguous prefix.
/// </summary>
public sealed class ReplicationJournal
{
    private readonly Dictionary<string, SortedDictionary<ulong, ReplicationRecord>> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> through = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>Appends a row idempotently and rejects a conflicting dot.</summary>
    public bool Append(ReplicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Dot.Sequence == 0 || string.IsNullOrWhiteSpace(record.Dot.Origin))
            throw new ArgumentException("A journal row requires a non-zero origin sequence.", nameof(record));
        lock (gate)
        {
            var origin = entries.GetValueOrDefault(record.Dot.Origin);
            if (origin is null)
            {
                origin = new SortedDictionary<ulong, ReplicationRecord>();
                entries.Add(record.Dot.Origin, origin);
            }
            if (origin.TryGetValue(record.Dot.Sequence, out var existing))
            {
                if (!string.Equals(ReplicationLattice.Digest(existing), ReplicationLattice.Digest(record), StringComparison.Ordinal))
                    throw new InvalidDataException("Two journal rows use the same replication dot with different contents.");
                return false;
            }
            origin.Add(record.Dot.Sequence, record);
            Advance(origin, record.Dot.Origin);
            return true;
        }
    }

    /// <summary>Returns a copy of the contiguous frontier for every origin.</summary>
    public IReadOnlyDictionary<string, ulong> Frontier
    {
        get
        {
            lock (gate) return new ReadOnlyDictionary<string, ulong>(new Dictionary<string, ulong>(through, StringComparer.Ordinal));
        }
    }

    /// <summary>Returns all rows sorted by origin and sequence.</summary>
    public IReadOnlyList<ReplicationRecord> Snapshot()
    {
        lock (gate)
        {
            return entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value.Values)
                .ToArray();
        }
    }

    public IReadOnlyList<string> Origins
    {
        get { lock (gate) return entries.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
    }

    public bool Contains(ReplicationDot dot)
    {
        lock (gate) return entries.TryGetValue(dot.Origin, out var origin) && origin.ContainsKey(dot.Sequence);
    }

    public ReplicationRecord? Get(ReplicationDot dot)
    {
        lock (gate) return entries.TryGetValue(dot.Origin, out var origin) && origin.TryGetValue(dot.Sequence, out var value) ? value : null;
    }

    /// <summary>Returns rows that fall in an inclusive range.</summary>
    public IReadOnlyList<ReplicationRecord> Read(ReplicationRange range)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(range.Origin, out var origin)) return [];
            return origin.Where(pair => pair.Key >= range.Start && pair.Key <= range.End).Select(pair => pair.Value).ToArray();
        }
    }

    /// <summary>Computes a stable digest over rows present in a range.</summary>
    public RangeFingerprint Fingerprint(ReplicationRange range)
    {
        var rows = Read(range);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows)
        {
            var digest = Encoding.UTF8.GetBytes(ReplicationLattice.Digest(row));
            hash.AppendData(digest);
        }
        return new(range, (ulong)rows.Count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>Returns exact missing sequence intervals above a peer frontier.</summary>
    public IReadOnlyList<ReplicationRange> Missing(IReadOnlyDictionary<string, ulong> peerFrontier)
    {
        ArgumentNullException.ThrowIfNull(peerFrontier);
        lock (gate)
        {
            var ranges = new List<ReplicationRange>();
            foreach (var pair in entries)
            {
                var start = peerFrontier.GetValueOrDefault(pair.Key) + 1;
                ulong? intervalStart = null;
                ulong previous = 0;
                foreach (var sequence in pair.Value.Keys.Where(value => value >= start))
                {
                    if (intervalStart is null) intervalStart = sequence;
                    else if (sequence != previous + 1)
                    {
                        ranges.Add(new(pair.Key, intervalStart.Value, previous));
                        intervalStart = sequence;
                    }
                    previous = sequence;
                }
                if (intervalStart is not null) ranges.Add(new(pair.Key, intervalStart.Value, previous));
            }
            return ranges;
        }
    }

    private void Advance(SortedDictionary<ulong, ReplicationRecord> origin, string originName)
    {
        var current = through.GetValueOrDefault(originName);
        while (origin.ContainsKey(current + 1)) current++;
        through[originName] = current;
    }
}

/// <summary>The rows and ranges a peer must exchange to converge.</summary>
public sealed record AntiEntropyPlan(
    IReadOnlyList<ReplicationRange> MissingFromLocal,
    IReadOnlyList<ReplicationRange> MissingFromRemote,
    IReadOnlyList<ReplicationRange> DivergentRanges)
{
    public bool IsEqual => MissingFromLocal.Count == 0 && MissingFromRemote.Count == 0 && DivergentRanges.Count == 0;
}

/// <summary>
/// Range-based set reconciliation. It refines only differing ranges and then
/// ships exact journal rows, so duplicate and reordered delivery are harmless.
/// </summary>
public sealed class AntiEntropyEngine
{
    private readonly ulong leafWidth;

    public AntiEntropyEngine(ulong leafWidth = 4096)
    {
        if (leafWidth == 0) throw new ArgumentOutOfRangeException(nameof(leafWidth));
        this.leafWidth = leafWidth;
    }

    public AntiEntropyPlan Plan(ReplicationJournal local, ReplicationJournal remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        var missingLocal = new List<ReplicationRange>();
        var missingRemote = new List<ReplicationRange>();
        var divergent = new List<ReplicationRange>();
        foreach (var origin in local.Origins.Concat(remote.Origins).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            var max = Math.Max(local.Frontier.GetValueOrDefault(origin), remote.Frontier.GetValueOrDefault(origin));
            var localRows = local.Snapshot().Where(row => row.Dot.Origin == origin).ToArray();
            var remoteRows = remote.Snapshot().Where(row => row.Dot.Origin == origin).ToArray();
            max = Math.Max(max, localRows.Select(row => row.Dot.Sequence).DefaultIfEmpty().Max());
            max = Math.Max(max, remoteRows.Select(row => row.Dot.Sequence).DefaultIfEmpty().Max());
            if (max == 0) continue;
            for (ulong start = 1; start <= max;)
            {
                var end = Math.Min(max, start + leafWidth - 1);
                var range = new ReplicationRange(origin, start, end);
                var localFingerprint = local.Fingerprint(range);
                var remoteFingerprint = remote.Fingerprint(range);
                if (localFingerprint.Digest != remoteFingerprint.Digest || localFingerprint.PresentCount != remoteFingerprint.PresentCount)
                {
                    var localSet = local.Read(range).Select(row => row.Dot.Sequence).ToHashSet();
                    var remoteSet = remote.Read(range).Select(row => row.Dot.Sequence).ToHashSet();
                    AddMissing(missingLocal, origin, remoteSet.Except(localSet), start, end);
                    AddMissing(missingRemote, origin, localSet.Except(remoteSet), start, end);
                    if (localSet.SetEquals(remoteSet) && !localFingerprint.Digest.Equals(remoteFingerprint.Digest, StringComparison.Ordinal))
                        divergent.Add(range);
                }
                if (end == ulong.MaxValue) break;
                start = end + 1;
            }
        }
        return new(missingLocal, missingRemote, divergent);
    }

    /// <summary>Copies source rows absent from destination and returns the count.</summary>
    public int Reconcile(ReplicationJournal destination, ReplicationJournal source)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        var copied = 0;
        foreach (var row in source.Snapshot())
            if (destination.Append(row)) copied++;
        return copied;
    }

    private static void AddMissing(List<ReplicationRange> result, string origin, IEnumerable<ulong> sequences, ulong minimum, ulong maximum)
    {
        var values = sequences.Where(sequence => sequence >= minimum && sequence <= maximum).OrderBy(value => value).ToArray();
        if (values.Length == 0) return;
        var start = values[0];
        var previous = start;
        foreach (var sequence in values.Skip(1))
        {
            if (sequence != previous + 1)
            {
                result.Add(new(origin, start, previous));
                start = sequence;
            }
            previous = sequence;
        }
        result.Add(new(origin, start, previous));
    }
}
