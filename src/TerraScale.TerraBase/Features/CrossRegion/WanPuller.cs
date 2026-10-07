using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.CrossRegion.V1;
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
    TimeProvider time, ILogger<WanPuller> logger) : BackgroundService
{
    /// <summary>Pause between rounds that found nothing more to pull.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    // Pull requests per origin in one round, so one busy origin cannot hold up the others.
    private const int MaxPullsPerOrigin = 16;

    // Stores this puller opened, by database, pinned until the engine deletes them; and each origin's applied prefix in
    // them, which only this puller advances (in-region peers never send another region's origins).
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IDatabaseStore> _stores = [with(StringComparer.Ordinal)];
    private readonly Dictionary<(IDatabaseStore Store, string Origin), long> _applied = [];

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
            }

            foreach (var key in _applied.Keys.Where(key => ReferenceEquals(key.Store, store)).ToList())
            {
                _ = _applied.Remove(key);
            }
        }
    }

    private async Task RunAsync(string region, string cell, CancellationToken stoppingToken)
    {
        // Failures are logged when they start, not every round; the metric counts every one.
        var failing = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var again = false;
            try
            {
                (again, var failures) = await RoundAsync(region, cell, quiet: failing, stoppingToken).ConfigureAwait(false);
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
    private async Task<(bool Again, int Failures)> RoundAsync(string region, string cell, bool quiet, CancellationToken cancellationToken)
    {
        var (members, listed, heads) = await HeadsAsync(cell, cancellationToken).ConfigureAwait(false);
        if (members == 0)
        {
            metrics.Unanswered(region);
            metrics.Failed(region, "heads");
            if (!quiet)
            {
                LogUnanswered(logger, region);
            }

            return (false, 1);
        }

        long lag = 0;
        var progressed = false;
        var failures = 0;
        var unregistered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (member, head) in heads)
        {
            if (WanProtocol.Database(head.TenantId, head.ProjectId, head.DatabaseId) is not { } database || head.Origin.Length != 16 || head.Sequence > long.MaxValue)
            {
                continue;
            }

            var origin = head.Origin.Memory;
            if (await TargetAsync(database, origin, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                _ = unregistered.Add(Name(database));
                continue;
            }

            var applied = await AppliedAsync(store, origin, cancellationToken).ConfigureAwait(false);
            if (applied < (long)head.Sequence)
            {
                try
                {
                    var pulled = await PullAsync(region, cell, member, database, store, origin, applied, (long)head.Sequence, cancellationToken).ConfigureAwait(false);
                    progressed |= pulled > applied;
                    applied = pulled;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    metrics.Failed(region, error switch
                    {
                        OriginAheadException => "ahead",
                        InvalidDataException or DocumentValidationException => "apply",
                        _ => "pull",
                    });
                    if (failures++ == 0 && !quiet)
                    {
                        LogPullFailed(logger, error, region);
                    }
                }
            }

            lag += Math.Max(0, (long)head.Sequence - applied);
        }

        // A listed member that did not answer holds origins this round could not measure: not caught up.
        metrics.Rounded(region, lag, members, unregistered.Count, caughtUp: lag == 0 && members >= listed, time.GetTimestamp());
        return (progressed && lag > 0, failures);
    }

    // Every member's heads, all pages: the members that answer the scattered request, and every other member they list,
    // asked on its own subject.
    private async Task<(int Members, int Listed, List<(string Member, Head Head)> Heads)> HeadsAsync(string cell, CancellationToken cancellationToken)
    {
        var answered = new Dictionary<string, List<Head>>(StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var first = new WanRequest { Version = WanProtocol.Version, Heads = new HeadsRequest() }.ToByteArray();
        await foreach (var data in bus.RequestManyAsync(WanProtocol.HeadsSubject(cell), first, cancellationToken).ConfigureAwait(false))
        {
            if (Parse(data) is { } reply && !answered.ContainsKey(reply.Member))
            {
                answered[reply.Member] = await PagesAsync(reply).ConfigureAwait(false);
                listed.UnionWith(reply.Members);
            }
        }

        var others = listed.Where(member => !answered.ContainsKey(member) && WanProtocol.IsMember(member)).ToList();
        var asked = await Task.WhenAll(others.Select(async member =>
        {
            try
            {
                return Parse(await bus.RequestAsync(WanProtocol.MemberSubject(cell, member), first, cancellationToken).ConfigureAwait(false)) is { } reply
                    && reply.Member == member
                    ? (member, await PagesAsync(reply).ConfigureAwait(false))
                    : (member, null);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A member that left or does not answer now: its origins wait for a later round.
                return (member, (List<Head>?)null);
            }
        })).ConfigureAwait(false);
        foreach (var (member, heads) in asked.Where(asked => asked.Item2 is not null))
        {
            answered[member] = heads!;
        }

        listed.UnionWith(answered.Keys);
        return (answered.Count, listed.Count(WanProtocol.IsMember), [.. answered.SelectMany(member => member.Value.Select(head => (member.Key, head)))]);

        // The heads of the member that sent reply, with the pages after it.
        async Task<List<Head>> PagesAsync(HeadsReply reply)
        {
            var heads = new List<Head>(reply.Heads);
            while (reply.More && reply.Heads.Count > 0)
            {
                var last = reply.Heads[^1];
                var next = new WanRequest { Version = WanProtocol.Version, Heads = new HeadsRequest { After = ByteString.CopyFrom([.. last.TenantId.Span, .. last.ProjectId.Span, .. last.DatabaseId.Span]) } };
                var member = reply.Member;
                reply = Parse(await bus.RequestAsync(WanProtocol.MemberSubject(cell, member), next.ToByteArray(), cancellationToken).ConfigureAwait(false)) is { } page && page.Member == member
                    ? page
                    : throw new InvalidDataException("a member answered a heads page with an invalid reply");
                heads.AddRange(reply.Heads);
            }

            return heads;
        }

        static HeadsReply? Parse(ReadOnlyMemory<byte> data)
        {
            try
            {
                var reply = data.Length <= WanProtocol.MaxMessageBytes ? HeadsReply.Parser.ParseFrom(data.Span) : null;
                // The member names the subject of its next requests: it must be a single subject token.
                return reply is not null && WanProtocol.IsMember(reply.Member) && reply.Heads.Count <= WanProtocol.MaxHeadsPerReply ? reply : null;
            }
            catch (InvalidProtocolBufferException)
            {
                return null;
            }
        }
    }

    // Pulls an origin from the member that holds it, up to the head it reported or the round's budget; returns the applied prefix.
    private async Task<long> PullAsync(
        string region, string cell, string member, TenantContext database, IDatabaseStore store, ReadOnlyMemory<byte> origin, long applied, long head,
        CancellationToken cancellationToken)
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
                    TenantId = WanProtocol.Id(database.OrganizationId),
                    ProjectId = WanProtocol.Id(database.ProjectId!),
                    DatabaseId = WanProtocol.Id(database.DatabaseId!),
                    Origin = ByteString.CopyFrom(origin.Span),
                    After = (ulong)applied,
                    Offset = (uint)received,
                },
            };
            var data = await bus.RequestAsync(subject, request.ToByteArray(), cancellationToken).ConfigureAwait(false);
            var reply = data.Length <= WanProtocol.MaxMessageBytes ? PullReply.Parser.ParseFrom(data.Span) : throw new InvalidDataException("the pull reply is too large");
            switch (reply.Status)
            {
                case PullReply.Types.Status.Ok when reply.Groups.Count > 0:
                    break;
                case PullReply.Types.Status.UnknownOrigin:
                    // The member no longer holds the origin; the next round's heads tell where it is, if anywhere.
                    return applied;
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
                var (sequence, _) = await journal.AppendAsync(
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
                metrics.Pulled(region, batch.EncodedApplicationBytes);
            }
        }

        return applied;
    }

    // The store of a database this region's catalog registers, opened (and created on a member that has no file yet) the
    // way the in-region receiver does: a database deleted meanwhile is not brought back.
    private async Task<IDatabaseStore?> TargetAsync(TenantContext database, ReadOnlyMemory<byte> origin, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stores.TryGetValue(Name(database), out var cached) && !cached.IsClosed)
            {
                return cached;
            }
        }

        var before = (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false))?.Incarnation;
        if (await tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is not true)
        {
            return null;
        }

        var (store, _, _, _) = await journal.OpenAsync(database, origin, cancellationToken).ConfigureAwait(false);
        if (await tenants.RegisteredAsync(database, cancellationToken).ConfigureAwait(false) is var registered && registered is not true)
        {
            if (registered is false && before != store.Incarnation)
            {
                _ = await engine.DeleteDatabaseAsync(database, cancellationToken, incarnation: store.Incarnation).ConfigureAwait(false);
            }

            return null;
        }

        lock (_gate)
        {
            _stores[Name(database)] = store;
        }

        return store;
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

    [LoggerMessage(LogLevel.Warning, "No member of region {Region} answered; retrying every round")]
    private static partial void LogUnanswered(ILogger logger, string region);

    [LoggerMessage(LogLevel.Warning, "Pulling an origin from region {Region} failed; retrying next round")]
    private static partial void LogPullFailed(ILogger logger, Exception error, string region);
}
