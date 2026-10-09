using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.CrossRegion;

/// <summary>
/// Pulls every other region's change groups into this member (<c>docs/cross-region.md</c>). Each round asks every member
/// of a region for the heads of the origins it holds, then pulls each origin this member is behind on from the member
/// that holds it, after this member's applied prefix, and applies the groups through <see cref="IReplicationJournal"/>:
/// in order, idempotently, each chained to the prefix by its digest, merged as in-region groups are. Only databases this
/// region's catalog registers under the same ids replicate. A round that pulled something and is still behind starts
/// the next at once; otherwise the next starts after <see cref="Interval"/>.
/// </summary>
/// <remarks>
/// anna-full (TS-643): every member pulls every remote origin itself, so a group crosses the WAN once per receiving member;
/// full Anna pulls once per region and joins the deltas into the cell. A group whose origin member lost its disk before
/// another region pulled it reaches that region only through a later write of the same document.
/// </remarks>
internal sealed partial class WanPuller(
    IRegionBus bus, IStorageEngine engine, IReplicationJournal journal, ITenantDirectory tenants, TerraBaseOptions options, WanMetrics metrics,
    TimeProvider time, ILogger<WanPuller> logger, IUsageMeter usage) : BackgroundService
{
    /// <summary>Pause between rounds that found nothing more to pull.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    // Pull requests per origin in one round, so one busy origin cannot hold up the others.
    private const int MaxPullsPerOrigin = 16;

    // Bound membership discovery and its requests; member names are at most 63 characters each.
    private const int MaxMembersPerRound = 1024;
    private const int MaxConcurrentHeads = 8;
    private const int MaxHeadsPagesPerMember = 16;
    // One member's full page allowance, shared by every member and page of a round: 1,024 members cannot hold 16 GiB.
    private const int MaxHeadsPerRound = MaxHeadsPagesPerMember * WanProtocol.MaxHeadsPerReply;
    private const long MaxHeadBytesPerRound = (long)MaxHeadsPagesPerMember * WanProtocol.MaxMessageBytes;
    private static readonly TimeSpan HeadsDeadline = TimeSpan.FromSeconds(10);

    // How long a database this region does not take is not asked about again.
    private static readonly TimeSpan RefusalTtl = TimeSpan.FromSeconds(10);

    // Stores this puller opened, by database, pinned until the engine deletes them; and each origin's applied prefix in
    // them, which only this puller advances (in-region peers never send another region's origins).
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IDatabaseStore> _stores = [with(StringComparer.Ordinal)];
    private readonly Dictionary<(IDatabaseStore Store, string Origin), long> _applied = [];
    private readonly Dictionary<string, (long At, DatabaseAdoption Why)> _refused = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _clashes = [with(StringComparer.Ordinal)];
    // By database, its origins that answered Ahead or sent a group conflicting with the applied journal. An origin stays
    // diverged until a group from it verifiably extends the applied journal, or the database is deleted here. Bounded.
    private readonly Dictionary<string, HashSet<string>> _diverged = [with(StringComparer.Ordinal)];
    private int _divergedOrigins;
    private const int MaxDivergedOrigins = 4096;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        engine.DatabaseDeleted += OnDeleted;
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        engine.DatabaseDeleted -= OnDeleted;
        base.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(options.Wan!.Remotes.Select(remote => Task.Run(() => RunAsync(remote.Key, remote.Value, stoppingToken), stoppingToken)));

    private void OnDeleted(object? sender, IDatabaseStore store)
    {
        lock (_gate)
        {
            if (store.Tenant is { } tenant)
            {
                _ = _stores.Remove(Name(tenant));
                if (_diverged.Remove(Name(tenant), out var origins))
                {
                    _divergedOrigins -= origins.Count;
                }
            }

            foreach (var key in _applied.Keys.Where(key => ReferenceEquals(key.Store, store)).ToList())
            {
                _ = _applied.Remove(key);
            }
        }
    }

    private bool Diverged(TenantContext database)
    {
        lock (_gate)
        {
            return _diverged.ContainsKey(Name(database));
        }
    }

    // Whether the diverged origin is retained; false once the bound is reached.
    private bool Diverge(TenantContext database, ReadOnlyMemory<byte> origin)
    {
        lock (_gate)
        {
            var key = Name(database);
            if (_diverged.TryGetValue(key, out var origins) && origins.Contains(Convert.ToHexString(origin.Span)))
            {
                return true;
            }

            if (_divergedOrigins >= MaxDivergedOrigins)
            {
                return false;
            }

            if (origins is null)
            {
                _diverged[key] = origins = [with(StringComparer.Ordinal)];
            }

            _ = origins.Add(Convert.ToHexString(origin.Span));
            _divergedOrigins++;
            return true;
        }
    }

    private void Converged(TenantContext database, ReadOnlyMemory<byte> origin)
    {
        lock (_gate)
        {
            var key = Name(database);
            if (_diverged.TryGetValue(key, out var origins) && origins.Remove(Convert.ToHexString(origin.Span)))
            {
                _divergedOrigins--;
                if (origins.Count == 0)
                {
                    _ = _diverged.Remove(key);
                }
            }
        }
    }

    private async Task RunAsync(string region, string cell, CancellationToken stoppingToken)
    {
        // Failures are logged when they start, not every round; the metric counts every one.
        var failing = false;
        TenantContext? after = null;
        // anna-full (TS-643): heads expose no authoritative retirement; unresolved identities block cleanup until answered.
        var discovery = new HeadsProgress();
        while (!stoppingToken.IsCancellationRequested)
        {
            var again = false;
            try
            {
                (again, var failures, after) = await RoundAsync(region, cell, after, discovery, quiet: failing, stoppingToken).ConfigureAwait(false);
                failing = failures > 0;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                metrics.Failed(region, "heads");
                if (!failing)
                {
                    LogRoundFailed(logger, error, region);
                }

                failing = true;
            }

            if (!again)
            {
                try
                {
                    await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    // Returns whether the round pulled groups and is still behind, and how many origins failed.
    private async Task<(bool Again, int Failures, TenantContext? Next)> RoundAsync(
        string region, string cell, TenantContext? after, HeadsProgress discovery, bool quiet, CancellationToken cancellationToken)
    {
        // Candidates predate the remote heads; a newer registration waits for the next scan. No storage admission
        // remains held while the WAN answers or while cleanup takes its own writer.
        if (options.CatalogPrimary is null && discovery.Records is null)
        {
            discovery.Records = await tenants.ReadDatabaseRecordsAsync(region, after, cancellationToken).ConfigureAwait(false);
            discovery.Candidates.UnionWith(discovery.Records.Databases.Select(Name));
        }

        var records = discovery.Records;
        var previouslyLagging = discovery.Lagging;
        // Heads can advance the saved sweep before pulling finishes. An interrupted round must not certify catch-up.
        discovery.Lagging = true;
        var (members, sweepEnded, heads) = await HeadsAsync(region, cell, discovery, cancellationToken).ConfigureAwait(false);
        // Unanswered discovery certifies nothing and measures no lag, but retained retries still pull from their members.
        var unanswered = heads.Count == 0 && members == 0;
        var complete = !unanswered && sweepEnded && !discovery.Invalid;
        if (unanswered)
        {
            metrics.Unanswered(region);
            metrics.Failed(region, "heads");
            if (!quiet)
            {
                LogUnanswered(logger, region);
            }

            if (discovery.Retry.Count == 0)
            {
                return (false, 1, after);
            }
        }

        if (records is not null && complete)
        {
            // A retry holds an older head: one certified absent must not adopt the database again right after forgetting it.
            var absent = records.Databases.Select(Name).Where(name => !discovery.Present.Contains(name)).ToHashSet(StringComparer.Ordinal);
            _ = discovery.Retry.RemoveAll(entry => WanProtocol.Database(entry.Head.TenantId, entry.Head.ProjectId, entry.Head.DatabaseId) is { } database
                && absent.Contains(Name(database)));
            await ForgetDeletedAsync(region, records, discovery.Present, cancellationToken).ConfigureAwait(false);
        }

        // A head on a page past a saved cursor is not advertised again until a fresh scan: retry those still behind. A member
        // lists one head per database, so its fresh head for that database supersedes the retry, whatever its origin.
        // Retries replicate only; they never witness a sweep.
        // Only a head this round can replicate supersedes; a malformed one is skipped below and must not drop the retry.
        var fresh = new HashSet<string>(heads.Where(entry => Replicable(entry.Head)).Select(entry => RetryKey(entry.Member, entry.Head)), StringComparer.Ordinal);
        var retrying = discovery.Retry.Select(entry => RetryKey(entry.Member, entry.Head)).ToHashSet(StringComparer.Ordinal);
        heads.AddRange(discovery.Retry.Where(entry => !fresh.Contains(RetryKey(entry.Member, entry.Head))));
        var retry = new List<(string Member, Head Head)>();
        long retryBytes = 0;
        long lag = 0;
        var progressed = false;
        var failures = 0;
        var unregistered = new HashSet<string>(StringComparer.Ordinal);
        var clashes = new HashSet<string>(StringComparer.Ordinal);
        var observations = new Dictionary<TenantContext, (long Remaining, bool Diverged)>();
        var behind = new HashSet<TenantContext>();
        foreach (var (member, head) in heads)
        {
            if (WanProtocol.Database(head.TenantId, head.ProjectId, head.DatabaseId) is not { } database || head.Origin.Length != 16 || head.Sequence > long.MaxValue)
            {
                continue;
            }

            var origin = head.Origin.Memory;
            // A head without a recorded home comes from a member that records none: the database is placed there only.
            var placement = new DatabasePlacement(head.HomeRegion.Length > 0 ? head.HomeRegion : region, [.. head.ReplicaRegions], (long)head.PlacementVersion);
            var (store, refused) = await TargetAsync(region, database, head.DatabaseName, placement, origin, cancellationToken).ConfigureAwait(false);
            if (store is null)
            {
                _ = (refused == DatabaseAdoption.NameTaken ? clashes : unregistered).Add(Name(database));
                continue;
            }

            var applied = await AppliedAsync(store, origin, cancellationToken).ConfigureAwait(false);
            var gone = false;
            // Only the origin's explicit Ahead answer or a journal conflict is divergence; a head snapshot may be stale.
            var untracked = false;
            var failed = false;
            // Bounded by this round's heads and retries, so every replicated database reports health each round.
            _ = observations.TryAdd(database, (0, false));
            // Any origin found behind starts the backlog, not only the first member's advertisement.
            if ((long)head.Sequence > applied && behind.Add(database))
            {
                usage.RecordReplicationState(database, region, (long)head.Sequence - applied, Diverged(database));
            }
            if (applied < (long)head.Sequence)
            {
                try
                {
                    var traffic = applied == 0 && head.Sequence > 1
                        || retrying.Contains(RetryKey(member, head))
                        ? ReplicationTrafficKind.Handoff : ReplicationTrafficKind.Delta;
                    (var pulled, gone) = await PullAsync(region, cell, member, database, store, origin, applied, (long)head.Sequence, traffic, cancellationToken).ConfigureAwait(false);
                    progressed |= pulled > applied;
                    applied = pulled;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    failed = true;
                    // A conflict past the bounded set is still reported for this round, and logged.
                    if (error is OriginAheadException or JournalConflictException && !Diverge(database, origin))
                    {
                        untracked = true;
                        LogDivergenceUntracked(logger, region);
                    }

                    metrics.Failed(region, error switch
                    {
                        OriginAheadException => "ahead",
                        InvalidDataException or JournalConflictException or DocumentValidationException => "apply",
                        _ => "pull",
                    });
                    if (failures++ == 0 && !quiet)
                    {
                        LogPullFailed(logger, error, region);
                    }
                }
            }

            // A pull can commit groups before it fails: count only what is still missing.
            if (failed)
            {
                try
                {
                    applied = Math.Max(applied, await AppliedAsync(store, origin, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The store failed too; the round's earlier prefix stays the estimate.
                }
            }

            var remaining = Math.Max(0, (long)head.Sequence - applied);
            if (observations.TryGetValue(database, out var observed))
            {
                observations[database] = (observed.Remaining + Math.Min(remaining, long.MaxValue - observed.Remaining), observed.Diverged || untracked);
            }
            lag += Math.Min(remaining, long.MaxValue - lag);
            // Bounded like one heads reply; anything beyond waits for a fresh scan to advertise it again. A member that no longer
            // holds the origin cannot serve a retry.
            if (remaining > 0 && !gone && retry.Count < WanProtocol.MaxHeadsPerReply && retryBytes + head.CalculateSize() <= WanProtocol.ReplyBudget)
            {
                retry.Add((member, head));
                retryBytes += head.CalculateSize();
            }
        }

        // Every retained diverged origin of a database counts, also one not advertised this round.
        foreach (var (database, observed) in observations)
        {
            usage.RecordReplicationState(database, region, observed.Remaining, observed.Diverged || Diverged(database));
        }

        discovery.Retry.Clear();
        discovery.Retry.AddRange(retry);

        // Earlier pages must also have reached their observed heads before a whole scan can reset staleness.
        discovery.Lagging = unanswered || previouslyLagging || lag > 0;
        if (unanswered)
        {
            return (progressed && lag > 0, failures + 1, after);
        }

        metrics.Rounded(region, lag, members, unregistered.Count, clashes.Count, caughtUp: complete && !discovery.Lagging, time.GetTimestamp());
        if (sweepEnded)
        {
            discovery.Records = null;
            discovery.Candidates.Clear();
            discovery.Present.Clear();
            discovery.Finished.Clear();
            discovery.After.Clear();
            discovery.Lagging = false;
            discovery.Invalid = false;
        }

        return (progressed && lag > 0, failures, complete ? records?.Next : after);
    }

    private static bool Replicable(Head head) =>
        WanProtocol.Database(head.TenantId, head.ProjectId, head.DatabaseId) is not null && head.Origin.Length == 16 && head.Sequence <= long.MaxValue;

    private static string RetryKey(string member, Head head) =>
        member + "/" + Convert.ToHexString([.. head.TenantId.Span, .. head.ProjectId.Span, .. head.DatabaseId.Span]);

    // A recorded home database absent from every member's complete scan was deleted there. The fixed catalog page
    // predates its heads requests; bounded witnesses cover earlier pages when the scan spans rounds. Only the primary
    // deletes; its followers follow its catalog. Restarting discards partial proof and starts a fresh catalog scan.
    private async Task ForgetDeletedAsync(string region, TenantDatabasePage records, HashSet<string> listed, CancellationToken cancellationToken)
    {
        foreach (var database in records.Databases)
        {
            if (!listed.Contains(Name(database)) && await tenants.ForgetDatabaseAsync(database, cancellationToken).ConfigureAwait(false))
            {
                LogForgot(logger, region);
            }
        }
    }

    // Every member's heads, all pages. Discover members from every reply, including direct replies and later pages:
    // different members may hold different views. Ask each discovered member at most once this round.
    private async Task<(int Members, bool SweepEnded, List<(string Member, Head Head)> Heads)> HeadsAsync(string region, string cell, HeadsProgress progress, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(HeadsDeadline, time);
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var deferredMembers = progress.Members;
        // Workers read only this snapshot; the caller records their cursor updates after each job finishes.
        var previousCursors = new Dictionary<string, ByteString>(progress.After, StringComparer.Ordinal);
        var collectedHeads = new Dictionary<string, List<Head>>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var finished = progress.Finished;
        var listed = new HashSet<string>(deferredMembers.Concat(finished), StringComparer.Ordinal);
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        // Members whose direct or continued pages were accepted this round queue behind the rest next round.
        var served = new HashSet<string>(StringComparer.Ordinal);
        var waiting = new Queue<string>(deferredMembers);
        var current = new Queue<string>();
        var queued = new HashSet<string>(deferredMembers, StringComparer.Ordinal);
        var continuations = new Dictionary<string, HeadsReply>(StringComparer.Ordinal);
        var pending = new List<Task<(string Member, HeadsPage? Page)>>();
        var first = new WanRequest { Version = WanProtocol.Version, Heads = new HeadsRequest { Region = options.Region } }.ToByteArray();
        var budget = new HeadsBudget(MaxHeadsPerRound, MaxHeadBytesPerRound);
        // Scattered first pages repeat every round: at most half the budget, so saved cursors and deferred members progress.
        var scatterBudget = new HeadsBudget(MaxHeadsPerRound / 2, MaxHeadBytesPerRound / 2);
        var invalid = false;
        var scattered = false;
        try
        {
            await foreach (var data in bus.RequestManyAsync(WanProtocol.HeadsSubject(cell), first, discovery.Token).ConfigureAwait(false))
            {
                if (Parse(data) is not { } reply)
                {
                    invalid = true;
                    continue;
                }

                if (!collectedHeads.ContainsKey(reply.Member))
                {
                    if (collectedHeads.Count == MaxMembersPerRound)
                    {
                        progress.CleanupBlocked = true;
                        invalid = true;
                        continue;
                    }

                    invalid |= !Ordered(reply.Heads, null);
                    if (!scatterBudget.TryTake(reply) || !budget.TryTake(reply))
                    {
                        // Over the round's budget: keep the names, leave the member unresolved and its cursor as it was.
                        Record(reply.Member, new([], new(reply.Members, StringComparer.Ordinal), false, null), initial: true);
                        continue;
                    }

                    Record(reply.Member, new([.. reply.Heads], new(reply.Members, StringComparer.Ordinal), !reply.More, null), initial: true);
                    if (reply.More)
                    {
                        continuations.Add(reply.Member, reply);
                    }
                }
            }

            scattered = true;
            while (!discovery.IsCancellationRequested)
            {
                while (pending.Count < MaxConcurrentHeads && attempted.Count < MaxMembersPerRound
                    && (current.TryDequeue(out var member) || waiting.TryDequeue(out member)))
                {
                    _ = queued.Remove(member);
                    if (!answered.Contains(member) && attempted.Add(member))
                    {
                        pending.Add(continuations.Remove(member, out var reply) ? ContinueAsync(reply) : AskAsync(member));
                    }
                }

                if (pending.Count == 0)
                {
                    break;
                }

                await FinishOneAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Keep collected heads useful when discovery times out, but never authorize deletion or reset staleness.
        }
        finally
        {
            try
            {
                await discovery.CancelAsync().ConfigureAwait(false);
                foreach (var (member, page) in await Task.WhenAll(pending).ConfigureAwait(false))
                {
                    if (page is { } collected)
                    {
                        Record(member, collected);
                    }
                }
            }
            finally
            {
                // Validation can fail before discovery returns; never lose taint after saving sweep proof.
                progress.Invalid |= invalid;
                // Unqueried work precedes failed attempts, and members served pages this round queue last. A cursor gets room
                // before retries when carryover overflows.
                var unresolved = current.Concat(waiting).Concat(listed.Where(member => !queued.Contains(member)).OrderBy(served.Contains))
                    .Where(member => !answered.Contains(member)).Distinct(StringComparer.Ordinal).ToList();
                if (unresolved.Count > MaxMembersPerRound)
                {
                    // Names we cannot remember may hold data: forgetting retry work must never authorize cleanup.
                    progress.CleanupBlocked = true;
                    unresolved = [.. unresolved.Where(progress.After.ContainsKey).Concat(unresolved).Distinct(StringComparer.Ordinal).Take(MaxMembersPerRound)];
                }

                deferredMembers.Clear();
                deferredMembers.AddRange(unresolved);
                var retained = new HashSet<string>(deferredMembers, StringComparer.Ordinal);
                foreach (var member in progress.After.Keys.Where(member => !retained.Contains(member)).ToList())
                {
                    _ = progress.After.Remove(member);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // An EOF-complete tainted sweep may be discarded; only RoundAsync can qualify it for cleanup or catch-up. Once
        // every listed member holds an EOF certificate, a deadline-cut re-scan counts like a failed one: it cannot veto.
        return (answered.Count, !progress.CleanupBlocked && scattered && finished.Count == listed.Count,
            [.. collectedHeads.SelectMany(member => member.Value.Select(head => (member.Key, head)))]);

        async Task FinishOneAsync()
        {
            var completedTask = await Task.WhenAny(pending).ConfigureAwait(false);
            _ = pending.Remove(completedTask);
            var (responding, page) = await completedTask.ConfigureAwait(false);
            if (page is { } collected)
            {
                Record(responding, collected);
            }
        }

        async Task<(string Member, HeadsPage? Page)> ContinueAsync(HeadsReply reply)
        {
            return (reply.Member, await PagesAsync(reply).ConfigureAwait(false));
        }

        void Record(string member, HeadsPage page, bool initial = false)
        {
            invalid |= page.Invalid;
            if (!collectedHeads.ContainsKey(member) && collectedHeads.Count == MaxMembersPerRound)
            {
                progress.CleanupBlocked = true;
                invalid = true;
                return;
            }

            // Current advertisements have their own budget; historical silence cannot reject usable current heads.
            var needsRoom = listed.Count + page.Members.Append(member).Distinct(StringComparer.Ordinal).Count(candidate => !listed.Contains(candidate)) > MaxMembersPerRound;
            foreach (var candidate in page.Members.Append(member))
            {
                if (!discovered.Contains(candidate) && discovered.Count == MaxMembersPerRound)
                {
                    progress.CleanupBlocked = true;
                    invalid = true;
                    continue;
                }

                _ = discovered.Add(candidate);
                var added = listed.Add(candidate);
                if (!answered.Contains(candidate) && !attempted.Contains(candidate) && queued.Add(candidate))
                {
                    (needsRoom && added ? current : waiting).Enqueue(candidate);
                }
            }

            collectedHeads[member] = page.Heads;
            if (!initial && page.Heads.Count > 0)
            {
                _ = served.Add(member);
            }

            // Keep only witnesses for the fixed catalog page. Record them before advancing any saved cursor.
            foreach (var head in page.Heads)
            {
                if (WanProtocol.Database(head.TenantId, head.ProjectId, head.DatabaseId) is not { } database
                    || head.Origin.Length != 16 || head.Sequence > long.MaxValue)
                {
                    invalid = true;
                    continue;
                }

                if (progress.Candidates.Contains(Name(database)))
                {
                    _ = progress.Present.Add(Name(database));
                }
            }

            if (!initial || page.Complete)
            {
                if (page.After is { } after)
                {
                    progress.After[member] = after;
                }
                else
                {
                    _ = progress.After.Remove(member);
                }
            }

            if (page.Complete)
            {
                _ = answered.Add(member);
                if (!finished.Contains(member) && finished.Count == MaxMembersPerRound)
                {
                    progress.CleanupBlocked = true;
                }
                else
                {
                    _ = finished.Add(member);
                }
            }
        }

        async Task<(string Member, HeadsPage? Page)> AskAsync(string member)
        {
            if (budget.Exhausted)
            {
                return (member, null);
            }

            try
            {
                _ = previousCursors.TryGetValue(member, out var after);
                if (Parse(await bus.RequestAsync(WanProtocol.MemberSubject(cell, member), first, discovery.Token).ConfigureAwait(false)) is { } reply
                    && reply.Member == member)
                {
                    return (member, budget.TryTake(reply)
                        ? await PagesAsync(reply).ConfigureAwait(false)
                        : new([], new(reply.Members, StringComparer.Ordinal), false, after));
                }

                return (member, new([], [], false, after, Invalid: true));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A member that left or does not answer now: its origins wait for a later round.
            }

            return (member, null);
        }

        // The heads of the member that sent reply, with the pages after it.
        async Task<HeadsPage> PagesAsync(HeadsReply reply)
        {
            var heads = new List<Head>(reply.Heads);
            var members = new HashSet<string>(reply.Members, StringComparer.Ordinal);
            _ = previousCursors.TryGetValue(reply.Member, out var after);
            try
            {
                if (!Ordered(reply.Heads, null))
                {
                    throw new InvalidDataException("the member sent unordered heads");
                }

                if (reply.More && reply.Heads.Count > 0)
                {
                    var firstAfter = Cursor(reply.Heads[^1]);
                    if (after is null || firstAfter.Span.SequenceCompareTo(after.Span) > 0)
                    {
                        after = firstAfter;
                    }
                }

                for (var pages = 1; reply.More && reply.Heads.Count > 0 && pages < MaxHeadsPagesPerMember && !budget.Exhausted; pages++)
                {
                    var next = new WanRequest { Version = WanProtocol.Version, Heads = new HeadsRequest { After = after!, Region = options.Region } };
                    var member = reply.Member;
                    var following = Parse(await bus.RequestAsync(WanProtocol.MemberSubject(cell, member), next.ToByteArray(), discovery.Token).ConfigureAwait(false), next.CalculateSize()) is { } page && page.Member == member
                        ? page
                        : throw new InvalidDataException("a member answered a heads page with an invalid reply");
                    if (following.Members.Distinct(StringComparer.Ordinal).Count(candidate => !members.Contains(candidate)) > MaxMembersPerRound - members.Count)
                    {
                        throw new InvalidDataException("the member lists too many members across its heads pages");
                    }

                    var last = following.Heads.Count > 0 ? Cursor(following.Heads[^1]) : null;
                    if (!Ordered(following.Heads, after) || (following.More && last is null))
                    {
                        throw new InvalidDataException("the member did not advance its heads page in order");
                    }

                    // Its names are bounded and validated: keep them even when its heads do not fit, so a newly listed holder
                    // is asked before any sweep can end.
                    members.UnionWith(following.Members);
                    if (!budget.TryTake(following))
                    {
                        // The cursor stays after the last accepted page; a later round resumes there.
                        break;
                    }

                    heads.AddRange(following.Heads);
                    reply = following;
                    after = last ?? after;
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // Earlier accepted pages remain useful when a later page fails or exceeds the discovery budget.
                return new(heads, members, false, after, Invalid: error is InvalidDataException);
            }

            // EOF finishes this member's full scan; retained candidate witnesses cover its earlier accepted pages.
            return new(heads, members, !reply.More, reply.More ? after : null);

            static ByteString Cursor(Head head)
            {
                if (WanProtocol.Database(head.TenantId, head.ProjectId, head.DatabaseId) is null)
                {
                    throw new InvalidDataException("the member sent an invalid heads cursor");
                }

                return Key(head);
            }
        }

        // The responder lists strictly increasing keys after the requested cursor. Any other order could move a cursor
        // past databases that no accepted page witnessed, so every head is checked, not only the last.
        static bool Ordered(IEnumerable<Head> heads, ByteString? after)
        {
            var previous = after;
            foreach (var head in heads)
            {
                var key = Key(head);
                if (previous is not null && key.Span.SequenceCompareTo(previous.Span) <= 0)
                {
                    return false;
                }

                previous = key;
            }

            return true;
        }

        static ByteString Key(Head head)
        {
            return ByteString.CopyFrom([.. head.TenantId.Span, .. head.ProjectId.Span, .. head.DatabaseId.Span]);
        }

        HeadsReply? Parse(ReadOnlyMemory<byte> data, int requestBytes = 0)
        {
            try
            {
                var reply = data.Length <= WanProtocol.MaxMessageBytes ? HeadsReply.Parser.ParseFrom(data.Span) : null;
                if (reply is not null)
                {
                    WanUsage.Heads(usage, region, reply, requestBytes == 0 ? first.Length : requestBytes, data.Length, responding: false);
                }

                // The member names the subject of its next requests: it must be a single subject token.
                return reply is not null && (!reply.More || reply.Heads.Count > 0)
                    && WanProtocol.IsMember(reply.Member) && reply.Heads.Count <= WanProtocol.MaxHeadsPerReply
                    && reply.Members.Count <= MaxMembersPerRound && reply.Members.All(WanProtocol.IsMember) ? reply : null;
            }
            catch (InvalidProtocolBufferException)
            {
                return null;
            }
        }
    }

    // Only lagging retry heads survive a round, beside one catalog page (100 candidates), its witnesses, bounded names and
    // fixed-size cursors.
    private sealed class HeadsProgress
    {
        public List<string> Members { get; } = [];

        public Dictionary<string, ByteString> After { get; } = [with(StringComparer.Ordinal)];

        public HashSet<string> Finished { get; } = [with(StringComparer.Ordinal)];

        // Heads still behind after the last round's pulls, at most one heads reply's worth.
        public List<(string Member, Head Head)> Retry { get; } = [];

        public TenantDatabasePage? Records { get; set; }

        public HashSet<string> Candidates { get; } = [with(StringComparer.Ordinal)];

        public HashSet<string> Present { get; } = [with(StringComparer.Ordinal)];

        public bool Lagging { get; set; }

        public bool Invalid { get; set; }

        public bool CleanupBlocked { get; set; }
    }

    // The heads one round holds in memory across every member and page, by count and serialized bytes.
    // Concurrent page workers reserve both counts under one lock, so a refused reservation is never visible.
    private sealed class HeadsBudget(long heads, long bytes)
    {
        private readonly Lock _gate = new();
        private long _heads = heads;
        private long _bytes = bytes;

        public bool Exhausted
        {
            get
            {
                lock (_gate)
                {
                    return _heads <= 0 || _bytes <= 0;
                }
            }
        }

        public bool TryTake(HeadsReply reply)
        {
            var size = reply.CalculateSize();
            lock (_gate)
            {
                if (reply.Heads.Count > _heads || size > _bytes)
                {
                    return false;
                }

                _heads -= reply.Heads.Count;
                _bytes -= size;
                return true;
            }
        }
    }

    private sealed record HeadsPage(List<Head> Heads, HashSet<string> Members, bool Complete, ByteString? After, bool Invalid = false);

    // Pulls an origin from the member that holds it, up to the head it reported or the round's budget; returns the applied
    // prefix, and whether the member no longer holds that origin.
    private async Task<(long Applied, bool Gone)> PullAsync(
        string region, string cell, string member, TenantContext database, IDatabaseStore store, ReadOnlyMemory<byte> origin, long applied, long head,
        ReplicationTrafficKind traffic, CancellationToken cancellationToken)
    {
        var subject = WanProtocol.MemberSubject(cell, member);
        byte[]? parts = null;
        var received = 0;
        for (var pulls = 0; applied < head && pulls < MaxPullsPerOrigin; pulls++)
        {
            var request = new WanRequest
            {
                Version = WanProtocol.Version,
                Pull = new PullRequest
                {
                    TrafficKind = traffic,
                    Region = options.Region,
                    TenantId = WanProtocol.Id(database.OrganizationId),
                    ProjectId = WanProtocol.Id(database.ProjectId!),
                    DatabaseId = WanProtocol.Id(database.DatabaseId!),
                    Origin = ByteString.CopyFrom(origin.Span),
                    After = (ulong)applied,
                    Offset = (uint)received,
                },
            };
            var payload = request.ToByteArray();
            usage.RecordReplicationBytes(database, region, WanUsage.Kind(traffic), true, payload.Length);
            var data = await bus.RequestAsync(subject, payload, cancellationToken).ConfigureAwait(false);
            usage.RecordReplicationBytes(database, region, WanUsage.Kind(traffic), false, data.Length);
            var reply = data.Length <= WanProtocol.MaxMessageBytes ? PullReply.Parser.ParseFrom(data.Span) : throw new InvalidDataException("the pull reply is too large");
            switch (reply.Status)
            {
                case PullReply.Types.Status.Ok when reply.Groups.Count > 0:
                    break;
                case PullReply.Types.Status.UnknownOrigin or PullReply.Types.Status.NotPlaced:
                    // The member no longer holds the origin; the next round's heads tell where it is, if anywhere.
                    return (applied, true);
                case PullReply.Types.Status.Ahead:
                    throw new OriginAheadException();
                default:
                    throw new IOException($"the member answered the pull with {reply.Status} and no groups");
            }

            foreach (var batch in reply.Groups)
            {
                if (batch.EncodedApplicationBytes > ReplicationProtocol.MaxApplicationBytes || batch.EventId is null || batch.EventId.Sequence != (ulong)applied + 1)
                {
                    throw new InvalidDataException("the member sent a change group out of order or oversized");
                }

                // A group larger than one reply arrives as parts of its envelope; it is applied once complete.
                if (batch.ApplicationEnvelope.Length < batch.EncodedApplicationBytes || received > 0)
                {
                    parts ??= new byte[batch.EncodedApplicationBytes];
                    if (reply.Groups.Count != 1 || batch.EncodedApplicationBytes != parts.Length || received + batch.ApplicationEnvelope.Length > parts.Length)
                    {
                        throw new InvalidDataException("the member sent an inconsistent part of a change group");
                    }

                    batch.ApplicationEnvelope.Span.CopyTo(parts.AsSpan(received));
                    received += batch.ApplicationEnvelope.Length;
                    if (received < parts.Length)
                    {
                        break;
                    }

                    batch.ApplicationEnvelope = UnsafeByteOperations.UnsafeWrap(parts);
                    (parts, received) = (null, 0);
                }

                var changes = ReplicationProtocol.Read(new SyncRequest { CommittedGroup = batch }, database, origin, ReplicationProtocol.Limits);
                var (sequence, _, newlyApplied) = await journal.AppendAsync(
                    store, origin, (long)batch.EventId.Sequence, changes, batch.PreviousJournalDigest.Memory, batch.JournalDigest.Memory, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _applied[(store, Convert.ToHexString(origin.Span))] = sequence;
                }

                if (sequence < (long)batch.EventId.Sequence)
                {
                    throw new InvalidDataException("the change group does not extend the applied prefix");
                }

                applied = sequence;
                if (newlyApplied)
                {
                    // Its journal proof extended the applied journal: whatever this origin diverged on is resolved.
                    Converged(database, origin);
                    usage.RecordReplicatedOperations(database, region, changes.Count);
                }
                metrics.Pulled(region, batch.EncodedApplicationBytes);
            }
        }

        return (applied, false);
    }

    // The store of a database this region serves, opened (and created on a member that has no file yet) the way the in-region
    // receiver does: a database deleted meanwhile is not brought back. The catalog primary first records a database of a
    // project this region registers under the same ids (AdoptDatabaseAsync), and takes a newer placement its home region
    // decided; a follower waits until its catalog has them. A database this region does not serve has its local copy
    // dropped. Otherwise why this region does not take it, asked again after RefusalTtl.
    private async Task<(IDatabaseStore? Store, DatabaseAdoption? Refused)> TargetAsync(
        string region, TenantContext database, string name, DatabasePlacement remote, ReadOnlyMemory<byte> origin, CancellationToken cancellationToken)
    {
        var key = Name(database);
        lock (_gate)
        {
            if (_refused.TryGetValue(key, out var refusal) && time.GetElapsedTime(refusal.At) < RefusalTtl)
            {
                return (null, refusal.Why);
            }
        }

        var primary = options.CatalogPrimary is null;
        var recorded = (await tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false))?.Placement;
        if (recorded is null)
        {
            var why = primary && name.Length > 0
                ? await tenants.AdoptDatabaseAsync(database, name, remote, cancellationToken).ConfigureAwait(false)
                : DatabaseAdoption.NotReplicated;
            if (why != DatabaseAdoption.Registered)
            {
                return Refuse(why);
            }

            recorded = remote;
        }
        else if (primary && remote.Version > recorded.Version && await tenants.UpdatePlacementAsync(database, remote, cancellationToken).ConfigureAwait(false))
        {
            recorded = remote;
        }

        if (!recorded.Includes(options.Region))
        {
            // Not served here (any more): this region's API answers 421 for it. A copy it had stays, unserved and no longer
            // pulled, so the writes this region accepted keep reaching the other regions; it is deleted with the database.
            // anna-full (TS-643): drop the copy once every other replica holds its writes and they are re-originated.
            return Refuse(DatabaseAdoption.NotReplicated);
        }

        lock (_gate)
        {
            if (_stores.TryGetValue(key, out var cached) && !cached.IsClosed)
            {
                return (cached, null);
            }
        }

        var before = (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false))?.Incarnation;
        if (await tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is not true)
        {
            return Refuse(DatabaseAdoption.NotReplicated);
        }

        var (store, _, _, _) = await journal.OpenAsync(database, origin, cancellationToken).ConfigureAwait(false);
        if (await tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is var registered && registered is not true)
        {
            if (registered is false && before != store.Incarnation)
            {
                _ = await engine.DeleteDatabaseAsync(database, cancellationToken, incarnation: store.Incarnation).ConfigureAwait(false);
            }

            return (null, DatabaseAdoption.NotReplicated);
        }

        lock (_gate)
        {
            _stores[key] = store;
        }

        return (store, null);

        (IDatabaseStore?, DatabaseAdoption?) Refuse(DatabaseAdoption why)
        {
            lock (_gate)
            {
                _refused[key] = (time.GetTimestamp(), why);
                if (why == DatabaseAdoption.NameTaken && _clashes.Add(key))
                {
                    LogNameTaken(logger, region);
                }
            }

            return (null, why);
        }
    }

    private async Task<long> AppliedAsync(IDatabaseStore store, ReadOnlyMemory<byte> origin, CancellationToken cancellationToken)
    {
        var key = (store, Convert.ToHexString(origin.Span));
        lock (_gate)
        {
            if (_applied.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        long applied;
        var read = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            applied = (await read.GetAppliedFrontierAsync(origin, cancellationToken).ConfigureAwait(false)).Sequence;
        }

        lock (_gate)
        {
            if (!store.IsClosed)
            {
                _applied[key] = applied;
            }
        }

        return applied;
    }

    private sealed class OriginAheadException() : Exception("this member applied change groups the origin never committed; restore recovery is required");

    private static string Name(TenantContext database) => $"{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}";

    [LoggerMessage(LogLevel.Warning, "Cross-region replication from region {Region} failed; retrying every round")]
    private static partial void LogRoundFailed(ILogger logger, Exception error, string region);

    [LoggerMessage(LogLevel.Warning, "A database of region {Region} has the name of another database of its project here, so it does not replicate; rename or delete one of them")]
    private static partial void LogNameTaken(ILogger logger, string region);

    [LoggerMessage(LogLevel.Information, "A database its home region {Region} deleted was deleted here too")]
    private static partial void LogForgot(ILogger logger, string region);

    [LoggerMessage(LogLevel.Warning, "No member of region {Region} answered; retrying every round")]
    private static partial void LogUnanswered(ILogger logger, string region);

    [LoggerMessage(LogLevel.Warning, "A diverged origin from region {Region} exceeds the tracked bound; it is reported diverged only while it fails")]
    private static partial void LogDivergenceUntracked(ILogger logger, string region);

    [LoggerMessage(LogLevel.Warning, "Pulling an origin from region {Region} failed; retrying next round")]
    private static partial void LogPullFailed(ILogger logger, Exception error, string region);
}
