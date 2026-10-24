using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

internal sealed partial class UsageMeter
{
    // A stripe holds at most this many distinct entries before it is published inline; a database's reads, writes and
    // request meters of one minute are a handful of entries.
    private const int MaxBuffered = 256;

    // Every read, write and request counts here first: in-place sums per processor, so the hot path takes an uncontended
    // lock and allocates nothing once its entry exists. Published into _pending in batches, under _flush before every read
    // of it (totals, flushes, month counts) and when a stripe is full.
    private readonly Stripe[] _stripes = [.. Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Stripe())];

    private sealed class Stripe
    {
        public readonly Lock Gate = new();
        public Dictionary<(UsageKey Key, UsageDimension? Dimension), Sums> Entries = [];
    }

    // Additive counts of one key, or of one meter dimension of a minute key. Saturating, as UsageReading.Add.
    private sealed class Sums
    {
        public long Reads, Writes, ReadUnits, WriteUnits, Count, Value;
        public long[]? Latency;

        public static long Plus(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private void Buffer(UsageKey key, long reads, long writes, long readUnits, long writeUnits)
    {
        var stripe = Current();
        lock (stripe.Gate)
        {
            var sums = Entry(stripe, (key, null));
            (sums.Reads, sums.Writes) = (Sums.Plus(sums.Reads, reads), Sums.Plus(sums.Writes, writes));
            (sums.ReadUnits, sums.WriteUnits) = (Sums.Plus(sums.ReadUnits, readUnits), Sums.Plus(sums.WriteUnits, writeUnits));
        }

        PublishIfFull(stripe);
    }

    // A minute's additive reading; readings that carry replication state go straight to _pending, which keeps the newest.
    private void Buffer(TenantContext database, DateTimeOffset? at, UsageReading reading)
    {
        var minute = (at ?? time.GetUtcNow()).ToUnixTimeSeconds() / 60;
        var key = new UsageKey(DatabaseOwner.Of(database), minute / 60, UsageScope.Minute, (int)(minute % 60));
        var stripe = Current();
        lock (stripe.Gate)
        {
            var sums = Entry(stripe, (key, reading.Dimension));
            (sums.Count, sums.Value) = (Sums.Plus(sums.Count, reading.Count), Sums.Plus(sums.Value, reading.Value));
            if (reading.Latency is { } latency)
            {
                sums.Latency ??= new long[latency.Length];
                for (var i = 0; i < latency.Length; i++)
                {
                    sums.Latency[i] = Sums.Plus(sums.Latency[i], latency[i]);
                }
            }
        }

        PublishIfFull(stripe);
    }

    /// <summary>Moves every buffered count into the pending counts. Callers that read the pending counts hold _flush.</summary>
    private void Publish()
    {
        foreach (var stripe in _stripes)
        {
            Publish(stripe);
        }
    }

    private void Publish(Stripe stripe)
    {
        Dictionary<(UsageKey Key, UsageDimension? Dimension), Sums> entries;
        lock (stripe.Gate)
        {
            if (stripe.Entries.Count == 0)
            {
                return;
            }

            (entries, stripe.Entries) = (stripe.Entries, []);
        }

        foreach (var ((key, dimension), sums) in entries)
        {
            Add(key, dimension is null
                ? new OpCounts(sums.Reads, sums.Writes, ReadUnits: sums.ReadUnits, WriteUnits: sums.WriteUnits)
                : new OpCounts(0, 0, new Dictionary<string, UsageReading>(StringComparer.Ordinal)
                {
                    [dimension.Key] = new(dimension, sums.Count, sums.Value, sums.Latency),
                }));
        }
    }

    private void PublishIfFull(Stripe stripe)
    {
        if (Volatile.Read(ref stripe.Entries).Count >= MaxBuffered)
        {
            Publish(stripe);
        }
    }

    private Stripe Current() => _stripes[(uint)Thread.GetCurrentProcessorId() % (uint)_stripes.Length];

    private static Sums Entry(Stripe stripe, (UsageKey, UsageDimension?) key)
    {
        if (!stripe.Entries.TryGetValue(key, out var sums))
        {
            sums = new Sums();
            stripe.Entries.Add(key, sums);
        }

        return sums;
    }
}
