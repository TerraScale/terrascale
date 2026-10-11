using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Features.Stats;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Usage;

/// <summary>The dimensions an observation read keeps, and the memory admission charged, in bytes, for what it keeps.</summary>
/// <param name="Keep">Whether a read keeps a dimension.</param>
/// <param name="Admit">Admits memory for what a read keeps.</param>
/// <param name="Hold">Holds admission for a whole document while it is parsed, before Keep filters it.</param>
internal sealed record ObservationFilter(Func<UsageDimension, bool> Keep, Action<long> Admit, Func<long, IDisposable> Hold);

/// <summary>Only the read and write totals of a persisted <see cref="OpCounts"/>; its meters are skipped, not materialized.</summary>
internal sealed record UsageTotals(long Reads, long Writes)
{
    public OpCounts Counts => new(Reads, Writes);
}

/// <summary>
/// Operation counts; persisted per managed database as document <c>ops</c> of its <c>usage</c> state collection (lifetime),
/// and as one document per UTC hour and per UTC day of its <c>usage-hours</c> and <c>usage-days</c> collections.
/// </summary>
/// <param name="Reads">Document reads.</param>
/// <param name="Writes">Document writes.</param>
/// <param name="Meters">Regional meter observations keyed by their bounded dimensions.</param>
internal sealed record OpCounts(long Reads, long Writes, IReadOnlyDictionary<string, UsageReading>? Meters = null)
{
    public static OpCounts Zero { get; } = new(0, 0);

    public static OpCounts operator +(OpCounts left, OpCounts right)
    {
        return new(left.Reads + right.Reads, left.Writes + right.Writes, UsageReading.Combine(left.Meters, right.Meters, 1));
    }

    public static OpCounts operator -(OpCounts left, OpCounts right)
    {
        return new(left.Reads - right.Reads, left.Writes - right.Writes, UsageReading.Combine(left.Meters, right.Meters, -1));
    }
}

/// <summary>The highest batch sequence a catalog primary added for one follower process.</summary>
/// <param name="Epoch">The process's epoch, base64.</param>
/// <param name="Sequence">The highest sequence added.</param>
internal sealed record UsageSenderEpoch(string Epoch, long Sequence);

/// <summary>
/// What a catalog primary added for one follower (by node name): its last epochs, oldest first. Saved as a document of the
/// catalog's <c>usage-senders</c> collection in the flush that saves the counts, so a batch sent again after the primary
/// restarted is recognized exactly when its counts were saved.
/// </summary>
/// <param name="Sender">The follower's node name.</param>
/// <param name="Epochs">Its last epochs.</param>
/// <param name="LastSeen">When the primary last received one of its batches, a retry included, by the primary's clock.</param>
internal sealed record UsageSender(string Sender, IReadOnlyList<UsageSenderEpoch> Epochs, DateTimeOffset LastSeen);

/// <summary>What a catalog primary did with a follower's batch.</summary>
internal enum UsageAccept
{
    /// <summary>Its counts were added.</summary>
    Added,

    /// <summary>It was added before: a retry, or an attempt that arrived late.</summary>
    Duplicate,

    /// <summary>It comes from a sender past the bound.</summary>
    TooManySenders,
}

/// <summary>Whether a follower batch was accepted, or why the catalog cannot be read now.</summary>
internal readonly union UsageAcceptResult(UsageAccept, Unavailable);

/// <summary>Which buckets counts belong in.</summary>
internal enum UsageScope
{
    /// <summary>Their hour, and its day.</summary>
    Hour,

    /// <summary>Only the day that starts at their hour: counts held past the kept hours.</summary>
    Day,

    /// <summary>Only the lifetime totals: counts held past the kept days.</summary>
    Lifetime,

    /// <summary>The minute, hour and day.</summary>
    Minute,
}

/// <summary>The counts of one database in one UTC hour.</summary>
/// <param name="Database">The database.</param>
/// <param name="Hour">Hours since the Unix epoch.</param>
/// <param name="Scope">Which buckets the counts belong in.</param>
/// <param name="Minute">Minute within the UTC hour for minute-scoped observations.</param>
internal readonly record struct UsageKey(DatabaseOwner Database, long Hour, UsageScope Scope = UsageScope.Hour, int Minute = 0)
{
    /// <summary>Whether the counts belong in a bucket of <paramref name="granularity"/>.</summary>
    public bool In(UsageGranularity granularity) => granularity == UsageWindow.Minute ? Scope == UsageScope.Minute
        : Scope is UsageScope.Hour or UsageScope.Minute || (Scope == UsageScope.Day && granularity == UsageWindow.Day);

    public long Bucket(UsageGranularity granularity) => (Hour * 60 + Minute) / granularity.Minutes;
}

/// <summary>Usage counts of a database: the lifetime totals and one entry per bucket of the window read.</summary>
/// <param name="Lifetime">The lifetime totals.</param>
/// <param name="Series">One entry per bucket.</param>
internal sealed record UsageCounts(OpCounts Lifetime, OpCounts[] Series);

/// <summary>Usage counts, or why they cannot be read now.</summary>
internal readonly union UsageCountsResult(UsageCounts, Unavailable);

/// <summary>
/// Counts operations in memory per database (organization, project and database: the request's tenant) and UTC hour, and
/// adds them to the catalog on <see cref="FlushAsync"/>. Counts of a database the catalog no longer names are dropped at the
/// flush. A catalog follower writes no catalog: it sends its counts to its primary instead (<see cref="SendAsync"/>).
/// </summary>
/// <remarks>
/// Memory holds one entry per database and hour with operations since the last flush. Before each flush or send, entries
/// past the kept hours fold into a day-only entry, and those past the kept days into a lifetime-only one, so a backlog (a
/// follower whose primary is away, a catalog that refuses writes) holds at most about 1,250 entries per database. A
/// follower sends folded entries with their scope, so the primary never saves them in an hour.
/// Entries are indexed by database; a flush or send drops the maps it emptied.
/// </remarks>
internal sealed partial class UsageMeter(SystemCatalog catalog, TimeProvider time, TerraBaseOptions options, ILogger<UsageMeter> logger, IMemoryBudget memory, IMeterFactory meters,
    IDatabaseStats stats)
    : IUsageMeter, IDisposable
{
    // Senders are peer-authenticated members; a cell has a handful.
    private const int MaxSenders = 1024;

    // A sender silent this long retires, so replaced nodes free their places. A follower retries an unacknowledged batch
    // every 10 seconds, each retry received keeps it from retiring, and it gives a batch up after UnsentLifetime: so no retry
    // comes after its sender's record is gone, even across an outage. A restarted follower starts a new epoch.
    private static readonly TimeSpan SenderRetirement = TimeSpan.FromDays(7);

    // Epochs (follower processes) remembered per sender, so a delayed attempt of a restarted process is still recognized; an
    // attempt lives at most its 10-second deadline, far less than 16 restarts take.
    private const int SenderEpochs = 16;

    private const string Senders = "usage-senders";

    /// <summary>Entries one follower batch carries at most.</summary>
    internal const int MaxBatch = 4096;

    /// <summary>
    /// How long a follower sends a batch again before it drops it: far less than a primary keeps the sequences of a silent
    /// sender, so a retry never arrives after they are gone, whatever outage came between. Its counts may then be lost
    /// (the primary may never have received them), but never counted twice.
    /// </summary>
    internal static readonly TimeSpan UnsentLifetime = TimeSpan.FromDays(1);

    /// <summary>
    /// Reads or writes one batch entry carries at most: 4,096 of them cannot overflow a total. A follower sends no more of an
    /// entry, which no real hour reaches.
    /// </summary>
    internal const long MaxCount = 1_000_000_000_000;

    // Byte meters aggregate past MaxCount (a day of storage samples, lifetime bandwidth); they are bounded only by long.
    internal static bool ByteMeter(string meter) => meter is "client_bytes" or "replication_bytes" or "storage_bytes";

    private const string Ops = "ops";

    // The id of the last flush that added to a database's counts, saved in the same write.
    private const string LastFlush = "flush";

    // Bucket documents are a few dozen bytes; this only bounds a page if one ever is not.
    private const long MaxPageBytes = 1024 * 1024;

    private static readonly OpCounts Read = new(1, 0);

    // Best-effort dashboard usage, not billing: counts live only here until a flush saves them, so a crash loses at most
    // those counted since the last successful flush. A failed flush keeps them for the next; a clean shutdown flushes.
    private readonly ConcurrentDictionary<DatabaseOwner, ConcurrentDictionary<UsageKey, OpCounts>> _pending = new();

    // Adds hold it shared; dropping emptied databases' maps holds it exclusively, so no Add writes to a dropped map.
    private readonly ReaderWriterLockSlim _maps = new();

    // On a primary: what each follower's batches added, and the senders whose state no flush saved yet. Accepting a batch
    // (its counts and its sequence) and a flush's snapshot of both are atomic under _accept.
    private readonly Lock _accept = new();
    private readonly Dictionary<string, UsageSender> _senders = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _unsavedSenders = [with(StringComparer.Ordinal)];

    // Retired senders whose saved state no flush deleted yet.
    private readonly HashSet<string> _retiredSenders = [with(StringComparer.Ordinal)];
    private Task<CatalogCheck>? _sendersLoaded;

    // Makes a flush (catalog commit plus removal from memory) atomic for readers, so totals never count twice or miss.
    private readonly SemaphoreSlim _flush = new(1, 1);

    // On a follower, one batch at a time goes to the primary, without holding _flush across the network.
    private readonly SemaphoreSlim _send = new(1, 1);

    // A flush whose write threw may still have committed (the catalog commits before it disposes the transaction): its
    // counts stay pending until its id, saved with them, shows whether they were saved. Guarded by _flush.
    private (string Id, KeyValuePair<UsageKey, OpCounts>[] Batch)? _inDoubt;

    // On a follower, the batch the primary has not acknowledged: sent again, with the same sequence, before any other. Kept
    // apart from _inDoubt, which a local usage read settles against this member's catalog. Guarded by _flush, and only
    // SendAsync (under _send) changes it.
    private (long Sequence, KeyValuePair<UsageKey, OpCounts>[] Batch, DateTimeOffset FirstSent)? _unsent;

    // The sequence of the last batch this follower process sent. Guarded by _send.
    private long _sent;

    // The pending keys still to send in turn, so every key gets into a batch even while others keep counting. Refilled
    // with every pending key once empty. Guarded by _flush.
    private readonly Queue<UsageKey> _turns = new();

    public void RecordRead(TenantContext database)
    {
        Add(new(DatabaseOwner.Of(database), CurrentHour()), Read);
        RecordOperation(database, "read", 1);
        stats.Read(database);
    }

    public void RecordWrites(TenantContext database, int writes)
    {
        Add(new(DatabaseOwner.Of(database), CurrentHour()), new OpCounts(0, writes));
        stats.Wrote(database, writes);
    }

    /// <summary>Databases with counts in memory.</summary>
    internal int PendingDatabases => _pending.Count;

    /// <summary>Adds counts.</summary>
    public void Add(UsageKey key, OpCounts counts)
    {
        if (counts.Meters is not null)
        {
            AddMeasurements(key, counts);
            return;
        }

        AddCore(key, counts);
    }

    private void AddCore(UsageKey key, OpCounts counts)
    {
        if (counts == OpCounts.Zero)
        {
            return;
        }

        _maps.EnterReadLock();
        try
        {
            _pending.GetOrAdd(key.Database, _ => new()).AddOrUpdate(key, counts, (_, current) => current + counts);
        }
        finally
        {
            _maps.ExitReadLock();
        }
    }

    /// <summary>
    /// On a catalog primary: adds a follower's batch unless its sequence is at or below the highest added for its sender's
    /// epoch (a retry, or an attempt that arrived late), also before this process started, once a flush saved it.
    /// </summary>
    public async ValueTask<UsageAcceptResult> AcceptAsync(
        string sender, string epoch, long sequence, IReadOnlyList<KeyValuePair<UsageKey, OpCounts>> counts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (await LoadSendersAsync(cancellationToken).ConfigureAwait(false) is Unavailable refused)
        {
            return refused;
        }
        lock (_accept)
        {
            var now = time.GetUtcNow();
            if (!_senders.TryGetValue(sender, out var state))
            {
                if (_senders.Count >= MaxSenders)
                {
                    RetireSenders(now);
                    if (_senders.Count >= MaxSenders)
                    {
                        return UsageAccept.TooManySenders;
                    }
                }

                state = new UsageSender(sender, [], now);
            }

            List<UsageSenderEpoch> epochs = [.. state.Epochs];
            var known = epochs.FindIndex(added => added.Epoch == epoch);
            if (known >= 0 && sequence <= epochs[known].Sequence)
            {
                // A retry keeps the sender from retiring, so its sequence outlives every retry; the next flush saves that.
                _senders[sender] = state with { LastSeen = now };
                _unsavedSenders.Add(sender);
                return UsageAccept.Duplicate;
            }

            foreach (var (key, count) in counts)
            {
                Add(key, count);
            }

            if (known >= 0)
            {
                epochs[known] = new(epoch, sequence);
            }
            else
            {
                epochs.Add(new(epoch, sequence));
                if (epochs.Count > SenderEpochs)
                {
                    epochs.RemoveAt(0);
                }
            }

            _senders[sender] = state with { Epochs = epochs, LastSeen = now };
            _unsavedSenders.Add(sender);
            _retiredSenders.Remove(sender);
            return UsageAccept.Added;
        }
    }

    /// <summary>
    /// Persisted plus not yet flushed counts: the lifetime totals and one entry per bucket of <paramref name="window"/>.
    /// <paramref name="proceed"/>, when set, runs before each catalog read (once a flush let this read in, after settling one
    /// in doubt, and before each page of the series) and throws to start none.
    /// </summary>
    public async ValueTask<UsageCountsResult> GetAsync(
        TenantContext database, UsageWindow window, CancellationToken cancellationToken, Action? proceed = null)
    {
        return await ReadAsync(database, window, meters: false, filter: null, proceed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Like <see cref="GetAsync"/>, with regional meters. <paramref name="filter"/>, when set, keeps only its dimensions of
    /// each entry as it is read and admits memory for what it keeps.
    /// </summary>
    public async ValueTask<UsageCountsResult> GetObservationsAsync(
        TenantContext database, UsageWindow window, CancellationToken cancellationToken, Action? proceed = null, ObservationFilter? filter = null) =>
        await ReadAsync(database, window, meters: true, filter, proceed, cancellationToken).ConfigureAwait(false);

    // Counts-only reads skip every entry's meters as it is read, so usage totals retain no analytics observations.
    private async ValueTask<UsageCountsResult> ReadAsync(
        TenantContext database, UsageWindow window, bool meters, ObservationFilter? filter, Action? proceed, CancellationToken cancellationToken)
    {

        OpCounts Shape(OpCounts counts)
        {
            return !meters ? Strip(counts, meters) : filter is null ? counts : Retain(counts, filter);
        }

        // Counts-only reads skip the meters JSON; analytics reads hold admission for the whole document while parsing it.
        OpCounts Parse(StoredDocument document)
        {
            if (!meters)
            {
                return JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.UsageTotals)!.Counts;
            }

            using var parsing = filter?.Hold(document.Json.Length * ParsedBytesPerJsonByte);
            return Shape(JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.OpCounts)!);
        }

        var owner = DatabaseOwner.Of(database);
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            proceed?.Invoke();
            if (_inDoubt is not null)
            {
                if (await SettleAsync(cancellationToken).ConfigureAwait(false) is Unavailable refused)
                {
                    return refused;
                }
                proceed?.Invoke();
            }

            var read = await catalog.ReadAsync<UsageCountsResult>(
                async transaction => new UsageCounts(
                    await transaction.GetAsync(UsageCollection(owner.DatabaseId), Ops, cancellationToken).ConfigureAwait(false) is { } document ? Parse(document) : OpCounts.Zero,
                    await ReadSeriesAsync(transaction, owner.DatabaseId, window, Parse, proceed, cancellationToken).ConfigureAwait(false)),
                static error => error, cancellationToken).ConfigureAwait(false);
            if (read is not UsageCounts saved)
            {
                return read switch
                {
                    Unavailable error => error,
                    UsageCounts or null => throw new UnreachableException(),
                };
            }

            var (lifetime, series) = saved;
            // A follower's own counts reach these totals once the primary saved them and they replicated here; adding the
            // ones still pending could count a batch twice, the primary having saved it before its acknowledgement came.
            foreach (var (key, entry) in options.CatalogPrimary is null && _pending.TryGetValue(owner, out var pending) ? pending : [])
            {
                var counts = Shape(entry);
                lifetime += counts;
                if (key.In(window.Granularity) && window.IndexOf(key.Hour, key.Minute) is { } index)
                {
                    series[index] += counts;
                }
            }

            // Only kept buckets have counts, saved or not: a saved bucket the next flush would prune reads as zero.
            var oldestKept = OldestKept(window.Granularity, CurrentMinute());
            for (var i = 0; i < series.Length && window.First + i < oldestKept; i++)
            {
                series[i] = OpCounts.Zero;
            }

            return new UsageCounts(lifetime, series);
        }
        finally
        {
            _flush.Release();
        }
    }

    /// <summary>Adds pending counts to the catalog in one transaction. On failure the counts stay pending.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        // The snapshot is taken under the lock, so overlapping flushes never persist the same counts twice.
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flush.Release();
        }
    }

    /// <summary>
    /// On a catalog follower: hands at most <see cref="MaxBatch"/> pending counts to <paramref name="send"/>, which delivers
    /// them to the primary under the batch's sequence (one more than the last batch's), and forgets them once it returns
    /// <see cref="Done"/> (the primary added them) or <see cref="Validation"/> (it refused them for good; they are dropped, so
    /// they cannot hold back later counts). A batch whose send threw, or that the primary lacked the capacity to take
    /// (<see cref="QuotaExceeded"/>), is sent again with the same sequence before any other, so the primary, which adds a
    /// batch only past the highest sequence it added for this process, never counts it twice. Returns whether a batch was
    /// sent and forgotten.
    /// </summary>
    public async ValueTask<bool> SendAsync(Func<long, KeyValuePair<UsageKey, OpCounts>[], CancellationToken, Task<AddUsageResult>> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long sequence;
            KeyValuePair<UsageKey, OpCounts>[] batch;
            await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = time.GetUtcNow();
                if (_unsent is { } stale && now - stale.FirstSent >= UnsentLifetime)
                {
                    foreach (var (key, counts) in stale.Batch)
                    {
                        Subtract(key, counts);
                    }

                    _unsent = null;
                    logger.ZLogWarning($"A usage batch of {stale.Batch.Length:@Entries} database hours went unacknowledged for a day; its counts are dropped");
                }

                Compact();
                (sequence, batch) = _unsent is { } unsent ? (unsent.Sequence, unsent.Batch) : (_sent + 1, NextBatch());
                if (batch.Length == 0)
                {
                    return false;
                }

                (_unsent, _sent) = ((sequence, batch, _unsent?.FirstSent ?? now), sequence);
            }
            finally
            {
                _flush.Release();
            }

            // Usage reads go on while the primary answers. If this throws or is cancelled, the batch stays unsent and goes
            // again with its sequence, which the primary adds once.
            var result = await send(sequence, batch, cancellationToken).ConfigureAwait(false);
            // Measurements are best effort; read and write totals are not. A primary that refuses the batch, for a measurement
            // it rejects or a scope it predates, or lacks the memory to read its measurements, gets the totals alone once,
            // under the same sequence it did not save.
            KeyValuePair<UsageKey, OpCounts>[] totals = [.. batch.Where(entry => entry.Value.Reads != 0 || entry.Value.Writes != 0)
                .Select(entry => KeyValuePair.Create(entry.Key, entry.Value with { Meters = null }))];
            if (result is not Done && totals.Length > 0 && (totals.Length < batch.Length || batch.Any(entry => entry.Value.Meters is not null)))
            {
                result = await send(sequence, totals, cancellationToken).ConfigureAwait(false);
                if (result is not QuotaExceeded)
                {
                    _measurementDrops.Add(1);
                }
            }

            if (result is QuotaExceeded)
            {
                // The primary added none of it: the whole batch stays unsent, like one whose send threw.
                return false;
            }

            await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _unsent = null;
                foreach (var (key, counts) in batch)
                {
                    Subtract(key, counts);
                }

                DropEmptied();
            }
            finally
            {
                _flush.Release();
            }

            if (result is Validation)
            {
                logger.ZLogWarning($"The catalog primary refused a usage batch of {batch.Length:@Entries} database hours; its counts are dropped");
            }

            return true;
        }
        finally
        {
            _send.Release();
        }
    }

    private async ValueTask FlushLockedAsync(CancellationToken cancellationToken)
    {
        Compact();
        if (await SettleAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
        {
            return;
        }
        KeyValuePair<UsageKey, OpCounts>[] batch;
        UsageSender[] senders;
        string[] retired;
        lock (_accept)
        {
            RetireSenders(time.GetUtcNow());
            batch = [.. Entries()];
            senders = [.. _unsavedSenders.Select(sender => _senders[sender])];
            retired = [.. _retiredSenders];
        }

        if (batch.Length == 0 && senders.Length == 0 && retired.Length == 0)
        {
            return;
        }

        // Empty flushes never initialize the catalog. Accept it before saving a nonempty batch.
        if (await catalog.CheckAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
        {
            return;
        }

        var id = EntityIds.New().ToString();
        int dropped;
        try
        {
            var written = await WriteAsync(id, batch, senders, retired, cancellationToken).ConfigureAwait(false);
            if (written is not int saved)
            {
                return;
            }

            dropped = saved;
        }
        catch
        {
            // The senders stay unsaved: the next flush saves their state again, whatever this one did.
            _inDoubt = (id, batch);
            throw;
        }

        foreach (var (key, counts) in batch)
        {
            Subtract(key, counts);
        }

        lock (_accept)
        {
            foreach (var saved in senders)
            {
                if (_senders.TryGetValue(saved.Sender, out var current) && ReferenceEquals(current, saved))
                {
                    _unsavedSenders.Remove(saved.Sender);
                }
            }

            _retiredSenders.ExceptWith(retired);
        }

        DropEmptied();

        if (dropped > 0)
        {
            logger.ZLogDebug($"Dropped operation counts of {dropped:@Databases} deleted databases");
        }
    }

    // Decides a flush that threw: it committed if any database it saved holds its id (one transaction saves all or none),
    // and then its counts leave memory; otherwise they stay pending for the next flush. A failed read leaves it in doubt.
    // Only a flush on the primary, which accepted the catalog first, leaves one in doubt.
    private async ValueTask<CatalogCheck> SettleAsync(CancellationToken cancellationToken)
    {
        if (_inDoubt is not { } doubt)
        {
            return new Done();
        }

        var committed = await catalog.ReadAsync<SettleResult>(async transaction =>
        {
            foreach (var database in doubt.Batch.Select(entry => entry.Key.Database.DatabaseId).Distinct())
            {
                if (await SystemCatalog.GetAsync(transaction, UsageCollection(database), LastFlush, TerraBaseJsonContext.Default.String, cancellationToken)
                    .ConfigureAwait(false) == doubt.Id)
                {
                    return true;
                }
            }

            return false;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (committed is not bool settled)
        {
            return committed switch
            {
                Unavailable refused => refused,
                bool => throw new UnreachableException(),
            };
        }

        if (settled)
        {
            foreach (var (key, counts) in doubt.Batch)
            {
                Subtract(key, counts);
            }
        }

        _inDoubt = null;
        return new Done();
    }

    // Returns how many databases of the batch no longer exist.
    private ValueTask<FlushResult> WriteAsync(
        string id, KeyValuePair<UsageKey, OpCounts>[] batch, UsageSender[] senders, string[] retired, CancellationToken cancellationToken)
    {
        var minute = CurrentMinute();
        return catalog.WriteAsync<FlushResult>(async transaction =>
        {
            foreach (var sender in senders)
            {
                await PutAsync(transaction, Senders, sender.Sender, sender, TerraBaseJsonContext.Default.UsageSender, cancellationToken).ConfigureAwait(false);
            }

            foreach (var sender in retired)
            {
                await transaction.DeleteAsync(Senders, sender, cancellationToken).ConfigureAwait(false);
            }

            var dropped = 0;
            foreach (var entries in batch.GroupBy(entry => entry.Key.Database))
            {
                var database = entries.Key;
                // Checked inside the write, which the engine serializes with a database delete: a delete that commits
                // later removes these counts with the database's state; one that committed earlier removed the database
                // from its project. Either way a deleted database gets no usage back. One point read per level.
                if (await EngineManagementService.DatabaseTenantUncheckedAsync(
                    transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is null)
                {
                    dropped++;
                    continue;
                }

                var usage = UsageCollection(database.DatabaseId);
                await AddAsync(transaction, usage, Ops, entries.Aggregate(OpCounts.Zero, (sum, entry) => sum + entry.Value), cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, usage, LastFlush, id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
                foreach (var granularity in UsageWindow.Granularities)
                {
                    var buckets = Collection(database.DatabaseId, granularity);
                    // Buckets past retention (counts held through a long outage) only add to the lifetime totals, and each
                    // flush deletes every expired bucket of the databases it saves: so a database holds at most the kept
                    // buckets and those that expired since its last flush.
                    var oldestKept = OldestKept(granularity, minute);
                    foreach (var bucket in entries.Where(entry => entry.Key.In(granularity)).GroupBy(entry => entry.Key.Bucket(granularity))
                        .Where(bucket => bucket.Key >= oldestKept))
                    {
                        var counts = bucket.Aggregate(OpCounts.Zero, (sum, entry) => sum + entry.Value);
                        await AddAsync(transaction, buckets, granularity.Id(bucket.Key), counts, cancellationToken).ConfigureAwait(false);
                    }

                    await PruneAsync(transaction, buckets, granularity.Id(oldestKept), cancellationToken).ConfigureAwait(false);
                }
            }

            return dropped;
        }, static error => error, cancellationToken);
    }

    private static async ValueTask AddAsync(IStorageTransaction transaction, string collection, string id, OpCounts counts, CancellationToken cancellationToken)
    {
        var saved = await SystemCatalog.GetAsync(transaction, collection, id, TerraBaseJsonContext.Default.OpCounts, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, collection, id, (saved ?? OpCounts.Zero) + counts, TerraBaseJsonContext.Default.OpCounts, cancellationToken).ConfigureAwait(false);
    }

    // Bucket ids sort by time, so the oldest come first. Bounded by what one database can hold (see WriteAsync).
    private static async ValueTask PruneAsync(IStorageTransaction transaction, string collection, string oldestKept, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await transaction.ListAsync(collection, afterId: null, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var expired = page.Documents.TakeWhile(document => string.CompareOrdinal(document.Id, oldestKept) < 0).ToList();
            foreach (var document in expired)
            {
                await transaction.DeleteAsync(collection, document.Id, cancellationToken).ConfigureAwait(false);
            }

            if (expired.Count < page.Documents.Count || !page.HasMore)
            {
                return;
            }
        }
    }

    // Up to MaxBatch pending entries, in turn: a key folded or sent meanwhile is skipped, and one added meanwhile waits for
    // the next round. A round of only skipped keys starts the next one at once.
    private KeyValuePair<UsageKey, OpCounts>[] NextBatch()
    {
        var batch = new List<KeyValuePair<UsageKey, OpCounts>>();
        var measurementBytes = 0;
        for (var round = 0; round < 2 && batch.Count == 0; round++)
        {
            if (_turns.Count == 0)
            {
                foreach (var (key, _) in Entries())
                {
                    _turns.Enqueue(key);
                }
            }

            while (batch.Count < MaxBatch && _turns.TryDequeue(out var key))
            {
                if (_pending.TryGetValue(key.Database, out var pending) && pending.TryGetValue(key, out var counts))
                {
                    var bytes = counts.Meters is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(new OpCounts(0, 0, counts.Meters), TerraBaseJsonContext.Default.OpCounts).Length;
                    if (bytes > MaxEntryMeasurementBytes)
                    {
                        // Folding merged more meters than one entry carries: send its counts and drop the meters, counted, so it
                        // neither waits forever nor makes the primary refuse the whole batch. A concurrent add retries next round.
                        var trimmed = counts with { Meters = null };
                        if (!pending.TryUpdate(key, trimmed, counts))
                        {
                            _turns.Enqueue(key);
                            break;
                        }

                        _measurementDrops.Add(1);
                        (counts, bytes) = (trimmed, 0);
                    }

                    if (measurementBytes + bytes > MaxBatchMeasurementBytes)
                    {
                        _turns.Enqueue(key);
                        break;
                    }

                    measurementBytes += bytes;
                    batch.Add(KeyValuePair.Create(key, counts));
                }
            }
        }

        return [.. batch];
    }

    // The first bucket a flush still saves: the kept buckets end with the current one.
    private static long OldestKept(UsageGranularity granularity, long minute) => (minute / granularity.Minutes) - granularity.Kept + 1;

    // Folds entries past the kept hours into a day-only entry at their day's first hour, and those past the kept days into
    // a lifetime-only entry at hour 0; a concurrent Add leaves an entry for the next pass. Under _flush. The keys of a batch
    // in doubt or unsent keep their counts, which settling or acknowledging it subtracts.
    private void Compact()
    {
        var minute = CurrentMinute();
        var hour = minute / 60;
        var held = (_inDoubt?.Batch ?? []).Concat(_unsent?.Batch ?? []).Select(entry => entry.Key).ToHashSet();
        foreach (var pending in _pending.Values)
        {
            foreach (var (key, counts) in pending)
            {
                var folded = key.Scope == UsageScope.Minute && key.Hour * 60 + key.Minute < OldestKept(UsageWindow.Minute, minute)
                    ? key with { Scope = UsageScope.Hour, Minute = 0 }
                    : key.Scope == UsageScope.Lifetime ? key
                    : key.Hour / 24 < OldestKept(UsageWindow.Day, minute) ? key with { Hour = 0, Scope = UsageScope.Lifetime }
                    : key.Scope == UsageScope.Hour && key.Hour < OldestKept(UsageWindow.Hour, minute) ? key with { Hour = key.Hour / 24 * 24, Scope = UsageScope.Day }
                    : key;
                if (folded != key && !held.Contains(key))
                {
                    FoldMeasurements(key, folded, counts);
                }
            }
        }
    }

    private IEnumerable<KeyValuePair<UsageKey, OpCounts>> Entries() => _pending.Values.SelectMany(pending => pending);

    // Drops the maps of databases with nothing pending, so a database counted once holds no memory for good. Under _flush.
    private void DropEmptied()
    {
        _maps.EnterWriteLock();
        try
        {
            foreach (var (owner, pending) in _pending)
            {
                if (pending.IsEmpty)
                {
                    _pending.TryRemove(owner, out _);
                }
            }
        }
        finally
        {
            _maps.ExitWriteLock();
        }
    }

    // Retires the senders silent for SenderRetirement; the next flush deletes their saved state. Under _accept.
    private void RetireSenders(DateTimeOffset now)
    {
        foreach (var sender in _senders.Values.Where(sender => now - sender.LastSeen >= SenderRetirement).ToList())
        {
            _senders.Remove(sender.Sender);
            _unsavedSenders.Remove(sender.Sender);
            _retiredSenders.Add(sender.Sender);
        }
    }

    // Loads what earlier processes saved of each sender once; a failed load is tried again by the next batch.
    private async ValueTask<CatalogCheck> LoadSendersAsync(CancellationToken cancellationToken)
    {
        Task<CatalogCheck> loading;
        lock (_accept)
        {
            if (_sendersLoaded is null or { IsFaulted: true } or { IsCanceled: true })
            {
                _sendersLoaded = LoadAsync();
            }

            loading = _sendersLoaded;
        }

        var result = await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result is Unavailable)
        {
            lock (_accept)
            {
                if (ReferenceEquals(_sendersLoaded, loading))
                {
                    _sendersLoaded = null;
                }
            }
        }

        return result;

        async Task<CatalogCheck> LoadAsync()
        {
            var read = await catalog.ReadAsync<SendersResult>(
                async transaction => await ListAllAsync(transaction, Senders, TerraBaseJsonContext.Default.UsageSender, CancellationToken.None).ConfigureAwait(false),
                static error => error, CancellationToken.None).ConfigureAwait(false);
            if (read is not List<UsageSender> saved)
            {
                return read switch
                {
                    Unavailable refused => refused,
                    List<UsageSender> => throw new UnreachableException(),
                };
            }

            lock (_accept)
            {
                foreach (var sender in saved)
                {
                    _senders.TryAdd(sender.Sender, sender);
                }
            }

            return new Done();
        }
    }

    private readonly union SettleResult(bool, Unavailable);
    private readonly union FlushResult(int, Unavailable);
    private readonly union SendersResult(List<UsageSender>, Unavailable);

    // proceed runs before each page, so a read that ends past a request's deadline starts no further one.
    private static OpCounts Strip(OpCounts counts, bool meters) => meters || counts.Meters is null ? counts : counts with { Meters = null };

    private static OpCounts Retain(OpCounts counts, ObservationFilter filter)
    {
        if (counts.Meters is null)
        {
            return counts;
        }

        var kept = counts.Meters.Where(entry => filter.Keep(entry.Value.Dimension)).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        filter.Admit(kept.Count * MeasurementBytes);
        return counts with { Meters = kept.Count == 0 ? null : kept };
    }

    private static async ValueTask<OpCounts[]> ReadSeriesAsync(
        IStorageTransaction transaction, string databaseId, UsageWindow window, Func<StoredDocument, OpCounts> parse, Action? proceed,
        CancellationToken cancellationToken)
    {
        var series = Enumerable.Repeat(OpCounts.Zero, window.Count).ToArray();
        var collection = Collection(databaseId, window.Granularity);
        var after = window.Granularity.Id(window.First - 1);
        var end = window.Granularity.Id(window.First + window.Count);
        while (true)
        {
            proceed?.Invoke();
            var page = await transaction.ListAsync(collection, after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var document in page.Documents)
            {
                if (string.CompareOrdinal(document.Id, end) >= 0)
                {
                    return series;
                }

                var index = window.Granularity.Parse(document.Id) - window.First;
                // Coarser analytics buckets read finer persisted buckets after the preceding boundary.
                if (index >= 0 && index < series.Length)
                {
                    series[index] += parse(document);
                }
            }

            if (!page.HasMore)
            {
                return series;
            }

            after = page.Documents[^1].Id;
        }
    }

    public void Dispose()
    {
        _flush.Dispose();
        _send.Dispose();
        _maps.Dispose();
        DisposeMeasurements();
    }

    private static string UsageCollection(string databaseId) => DatabaseState(databaseId) + "usage";

    private static string Collection(string databaseId, UsageGranularity granularity) => DatabaseState(databaseId) + granularity.Collection;

    private long CurrentMinute() => time.GetUtcNow().ToUnixTimeSeconds() / 60;

    private long CurrentHour() => time.GetUtcNow().ToUnixTimeSeconds() / 3600;

    // Lock-free: retries when a concurrent Add changed the entry; removes it once nothing is pending.
    private void Subtract(UsageKey key, OpCounts counts)
    {
        if (!_pending.TryGetValue(key.Database, out var pending))
        {
            return;
        }

        while (pending.TryGetValue(key, out var current))
        {
            var rest = current - counts;
            if (rest == OpCounts.Zero ? pending.TryRemove(KeyValuePair.Create(key, current)) : pending.TryUpdate(key, rest, current))
            {
                ReleaseMeasurement(key, rest);
                return;
            }
        }
    }
}
