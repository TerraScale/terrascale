using NATS.Client.KeyValueStore;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Platform;
using TerraBase.Errors;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed partial class RegionalHandoff
{
    private const string Preparing = "preparing";
    private const string Activating = "activating";
    private const string Retiring = "retiring";
    private const string Recovering = "recovering";
    private const int ProtocolVersion = 2;

    private enum PendingStep
    {
        Restart,
        Readiness,
        CatchUp
    }

    private PendingStep? _lastRetiringStep;
    private TenantContext? _checkedAfter;
    private bool _checked;
    private readonly ConcurrentDictionary<string, DatabaseFence> _fenced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _walkFenced = new(StringComparer.Ordinal);
    private sealed record DatabaseFence(TenantContext Database, FencedDatabase Status);
    private sealed record LocalDataRefusal(HomeRefusal Kind, string Detail);
    private long _unprovenEpoch;
    private bool _pageCaughtUp;
    private RegionalOwner? _startupOwner;
    private bool _protocolPublished;
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatchUpBudget = TimeSpan.FromSeconds(1);
    private const int CatchUpParallelism = 16;
    private static readonly TimeSpan ActivationBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(2);

    private async Task RenewWriterAsync(INatsKVStore kv, NatsKVEntry<byte[]> entry, RegionalOwner owner, CancellationToken token)
    {
        if (_writerLease is not { } writer)
        {
            return;
        }
        if (owner.Transfer is { Phase: Preparing, Source: var draining } && draining == options.MachineId)
        {
            return;
        }
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(AttemptBudget);
        try
        {
            if (!await PreparedOwnerAsync(kv, owner, attempt.Token).ConfigureAwait(false))
            {
                return;
            }
            if (await writer.RenewAsync(owner.Epoch, attempt.Token).ConfigureAwait(false) is null)
            {
                return;
            }
            if (!owners.Writer)
            {
                _activeEpoch = 0;
            }
            if (!await ActivateAsync(owner, attempt.Token).ConfigureAwait(false)
                || owner is { Activated: false, Transfer: { Phase: Activating } transfer } && transfer.Deadline <= DateTimeOffset.UtcNow)
            {
                return;
            }

            if (await writer.RenewAsync(owner.Epoch, attempt.Token).ConfigureAwait(false) is not { } until)
            {
                return;
            }
            if (!owner.Activated || owner.Version != ProtocolVersion)
            {
                var active = owner with
                {
                    Activated = true,
                    Version = ProtocolVersion,
                    Previous = owner.Previous ?? (owner.Epoch > 1 ? options.MachineId : null),
                    Transfer = owner.Transfer is { Phase: Activating } pending ? pending with { Phase = Retiring }
                        : owner.Transfer is { Phase: Recovering } ? null : owner.Transfer,
                };
                _ = await kv.UpdateAsync(options.Region, Encode(active), entry.Revision, cancellationToken: attempt.Token).ConfigureAwait(false);
                Observe(active);
            }
            if (!_protocolPublished)
            {
                _ = await kv.PutAsync("protocol." + options.Region, "2"u8.ToArray(), cancellationToken: attempt.Token).ConfigureAwait(false);
                _protocolPublished = true;
            }
            owners.Resume();
            ResumePromoted();
            owners.Renew(until);
            await RecheckFencedAsync(owner, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            LogRenewalFailed(logger, options.Region, "timed_out", null, null, nameof(OperationCanceledException));
        }
        catch (NatsKVWrongLastRevisionException)
        {
        }
    }

    private async Task<bool> RecoverTransferAsync(INatsKVStore kv, NatsKVEntry<byte[]> entry, RegionalOwner owner, CancellationToken token)
    {
        if (owner.Transfer is not { } transfer)
        {
            if (owner.Version == 0 || owner.Activated || owner.Previous is null || entry.Created + ActivationBudget > DateTimeOffset.UtcNow)
            {
                return false;
            }
            transfer = new RegionalTransfer(Guid.NewGuid().ToString("N"), owner.Previous, owner.Node, "", Activating, entry.Created + ActivationBudget);
        }
        var abandonedHere = transfer.Phase == Preparing && transfer.Source == options.MachineId;
        if ((!abandonedHere && transfer.Deadline > DateTimeOffset.UtcNow) || transfer.Phase == Retiring)
        {
            return false;
        }
        if (transfer.Phase == Recovering)
        {
            if (owner.Activated || await NodeReadyAsync(owner.Node, token).ConfigureAwait(false))
            {
                return false;
            }
            var targetProof = await ReadAsync(kv, PreparedKey(transfer.Target), token).ConfigureAwait(false);
            if (targetProof?.Value is not { } proofBytes
                || JsonSerializer.Deserialize(proofBytes, ReplicasJsonContext.Default.RegionalPrepared) is not { } proof
                || proof.Source != transfer.Source || proof.Target != transfer.Target
                || proof.Operation != transfer.Id || proof.Epoch != transfer.PreparedEpoch
                || !await NodeReadyAsync(transfer.Target, token).ConfigureAwait(false))
            {
                return false;
            }
            var fallback = new RegionalOwner(options.Region, transfer.Target, owner.Epoch + 1, owner.Node,
                Transfer: transfer with { Phase = Activating, Deadline = DateTimeOffset.UtcNow + ActivationBudget },
                Version: ProtocolVersion);
            return await CommitRecoveryAsync(kv, entry, fallback, transfer.Id, token).ConfigureAwait(false);
        }

        RegionalOwner recovered;
        if (transfer.Phase == Activating && !owner.Activated)
        {
            if (!await NodeReadyAsync(transfer.Source, token).ConfigureAwait(false))
            {
                if (await HasPreparedAsync(kv, owner, token).ConfigureAwait(false)
                    && await NodeReadyAsync(owner.Node, token).ConfigureAwait(false))
                {
                    return await CommitRecoveryAsync(kv, entry,
                        owner with { Transfer = transfer with { Deadline = DateTimeOffset.UtcNow + ActivationBudget } },
                        transfer.Id, token).ConfigureAwait(false);
                }
                return false;
            }
            recovered = new RegionalOwner(options.Region, transfer.Source, owner.Epoch + 1, owner.Node,
                Transfer: transfer with { Phase = Recovering, Deadline = DateTimeOffset.UtcNow + ActivationBudget }, Version: ProtocolVersion);
        }
        else if (transfer.Phase == Preparing && (owner.Node == transfer.Source || owner.Node == transfer.Target))
        {
            recovered = owner.Node != transfer.Source ? owner with { Transfer = null }
                : new RegionalOwner(options.Region, owner.Node, owner.Epoch + 1, owner.Node,
                    Transfer: transfer with { Phase = Recovering, Source = owner.Node, Deadline = DateTimeOffset.UtcNow + ActivationBudget }, Version: ProtocolVersion);
        }
        else
        {
            return false;
        }

        return await CommitRecoveryAsync(kv, entry, recovered, transfer.Id, token).ConfigureAwait(false);
    }

    private async Task<bool> CommitRecoveryAsync(INatsKVStore kv, NatsKVEntry<byte[]> entry, RegionalOwner recovered, string operation, CancellationToken token)
    {
        try
        {
            _ = await kv.UpdateAsync(options.Region, Encode(recovered), entry.Revision, cancellationToken: token).ConfigureAwait(false);
            Observe(recovered);
            logger.ZLogWarning($"Regional transfer recovery selected {recovered.Node:@Node}, epoch {recovered.Epoch:@Epoch}, operation {operation:@Operation}");
        }
        catch (NatsKVWrongLastRevisionException)
        {
        }
        return true;
    }

    private async Task<bool> NodeReadyAsync(string node, CancellationToken token)
    {
        if (node == options.MachineId)
        {
            return await services.GetRequiredService<Health.INodeReadiness>().IsReadyAsync(token).ConfigureAwait(false);
        }
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(AttemptBudget);
        try
        {
            var peer = await SelfRegionPeerAsync(node, attempt.Token).ConfigureAwait(false);
            if (peer is null)
            {
                return false;
            }
            return await StatusAsync(new Uri(peer.InternalListener(options.InternalPort), "/v1/rollout/status"), attempt.Token).ConfigureAwait(false)
                is { Ready: true, Node: var actual } && actual == node;
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RecoverUncommittedAsync(INatsKVStore kv)
    {
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var entry = await ReadAsync(kv, options.Region, recovery.Token).ConfigureAwait(false);
            if (entry is null)
            {
                return;
            }
            var owner = Decode(entry.Value.Value!);
            if (owner.Transfer is { } transfer && transfer.Source == options.MachineId && transfer.SourceSession == _session)
            {
                RegionalOwner? resumed = transfer.Phase == Preparing
                    ? owner.Node != transfer.Source ? owner with { Transfer = null }
                        : new RegionalOwner(options.Region, owner.Node, owner.Epoch + 1, owner.Node,
                            Transfer: transfer with { Phase = Recovering, Source = owner.Node, Deadline = DateTimeOffset.UtcNow + ActivationBudget }, Version: ProtocolVersion)
                    : transfer.Phase == Activating && !owner.Activated
                        ? new RegionalOwner(options.Region, options.MachineId, owner.Epoch + 1, owner.Node,
                            Transfer: transfer with { Phase = Recovering, Deadline = DateTimeOffset.UtcNow + ActivationBudget }, Version: ProtocolVersion)
                        : null;
                if (resumed is not null)
                {
                    _ = await kv.UpdateAsync(options.Region, Encode(resumed), entry.Value.Revision, cancellationToken: recovery.Token).ConfigureAwait(false);
                    owner = resumed;
                }
            }
            Observe(owner);
            ResumePromoted();
        }
        catch (Exception error)
        {
            logger.ZLogWarning(error, $"Regional handoff recovery retries from its durable phase; this process stays fenced until quorum confirmation");
        }
    }

    private async Task WritePreparedAsync(INatsKVStore kv, RegionalPrepared prepared, CancellationToken token)
    {
        _ = await kv.PutAsync(PreparedKey(prepared.Target),
            JsonSerializer.SerializeToUtf8Bytes(prepared, ReplicasJsonContext.Default.RegionalPrepared), cancellationToken: token).ConfigureAwait(false);
    }

    private string PreparedKey(string target) => "prepared." + options.Region + "." + target;

    private async Task<bool> HasPreparedAsync(INatsKVStore kv, RegionalOwner owner, CancellationToken token) =>
        await PreparedAsync(kv, owner, token).ConfigureAwait(false) is not null;

    private async Task<RegionalPrepared?> PreparedAsync(INatsKVStore kv, RegionalOwner owner, CancellationToken token)
    {
        var entry = await ReadAsync(kv, PreparedKey(owner.Node), token).ConfigureAwait(false);
        if (entry?.Value is not { } bytes
            || JsonSerializer.Deserialize(bytes, ReplicasJsonContext.Default.RegionalPrepared) is not { } prepared || prepared.Target != owner.Node)
        {
            return null;
        }

        var source = owner.Previous ?? (owner.Version == 0 ? owner.Node : null);
        var current = prepared.Source == source && prepared.Epoch == owner.Epoch - 1;
        return owner.Transfer is { Phase: Activating } transfer
            ? transfer.Id == prepared.Operation && (current
                || transfer.Target == owner.Node && prepared.Source == transfer.Source && prepared.Epoch == transfer.PreparedEpoch) ? prepared : null
            : current ? prepared : null;
    }

    private async Task<bool> PreparedOwnerAsync(INatsKVStore kv, RegionalOwner owner, CancellationToken token)
    {
        if (owner is { Epoch: 1, Previous: null })
        {
            return true;
        }
        if (owner.Transfer is { Phase: Recovering } recovered && recovered.Source == owner.Node)
        {
            await WritePreparedAsync(kv, new RegionalPrepared(owner.Previous!, owner.Node, owner.Epoch - 1, recovered.Id), token).ConfigureAwait(false);
            return true;
        }
        if (await PreparedAsync(kv, owner, token).ConfigureAwait(false) is { } prepared)
        {
            if (owner is { Transfer: { Phase: Activating } transfer, Previous: { } previous } && prepared.Epoch != owner.Epoch - 1)
            {
                await WritePreparedAsync(kv, new RegionalPrepared(previous, owner.Node, owner.Epoch - 1, transfer.Id), token).ConfigureAwait(false);
            }
            return true;
        }
        if (owner.Version == 0 && _startupOwner == owner
            && await ReadAsync(kv, "protocol." + options.Region, token).ConfigureAwait(false) is null)
        {
            await WritePreparedAsync(kv, new RegionalPrepared(owner.Previous ?? owner.Node, owner.Node, owner.Epoch - 1, null), token).ConfigureAwait(false);
            return true;
        }
        if (_unprovenEpoch != owner.Epoch)
        {
            _unprovenEpoch = owner.Epoch;
            services.GetRequiredService<HomeLeases>().Refused(HomeRefusal.Unproven, "writer");
            logger.ZLogWarning($"Regional writer refused: the owner record names this node at epoch {owner.Epoch:@Epoch} without a certificate this node wrote in a drain");
        }
        return false;
    }

    private async Task<bool> LocalDataCurrentAsync(RegionalOwner owner, IReplicaCatalog catalog, CancellationToken token)
    {
        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        do
        {
            if (await catalog.ListHomedAsync(options.Region, _checkedAfter, token).ConfigureAwait(false) is not TenantDatabasePage page)
            {
                return false;
            }
            if (!_pageCaughtUp)
            {
                _pageCaughtUp = true;
                IReadOnlyList<TenantContext> unheld = [.. page.Databases.Where(database => Compared(database) && replicas.Holding(database) is not HeldAt)];
                if (await CatchUpAsync(unheld, token).ConfigureAwait(false) is > 0 and var late)
                {
                    logger.ZLogWarning($"The activation check could not catch up {late:@Late} of {unheld.Count:@Databases} local replicas within {CatchUpBudget.TotalSeconds:@Seconds} s; a database whose replica position stays unknown stays fenced until a later attempt reads it");
                }
            }

            foreach (var database in page.Databases)
            {
                if (Compared(database) && await RefusalAsync(database, token).ConfigureAwait(false) is { } refusal)
                {
                    Fence(database, refusal, owner.Epoch);
                    _walkFenced.Add(database.DatabaseId!);
                }
                else
                {
                    Unfence(database.DatabaseId!);
                }
                _checkedAfter = database;
            }
            _checkedAfter = page.Next;
            _pageCaughtUp = false;
        }
        while (_checkedAfter is not null);
        foreach (var id in _fenced.Keys.Where(id => !_walkFenced.Contains(id)))
        {
            Unfence(id);
        }
        _walkFenced.Clear();
        _checked = true;
        return true;
    }

    private async Task RecheckFencedAsync(RegionalOwner owner, CancellationToken token)
    {
        if (_fenced.IsEmpty)
        {
            return;
        }

        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        var leases = services.GetRequiredService<HomeLeases>();
        IReadOnlyList<DatabaseFence> fenced = [.. _fenced.Values];
        _ = await CatchUpAsync([.. fenced.Select(fence => fence.Database).Where(database => replicas.Holding(database) is PositionUnknown)], token).ConfigureAwait(false);
        foreach (var fence in fenced)
        {
            if (Compared(fence.Database) && await RefusalAsync(fence.Database, token).ConfigureAwait(false) is { } refusal)
            {
                Fence(fence.Database, refusal, owner.Epoch);
            }
            else if (await leases.TakeRegionalAsync(fence.Database, owner.Previous ?? options.MachineId, token).ConfigureAwait(false))
            {
                Unfence(fence.Database.DatabaseId!);
            }
        }
    }

    private bool Compared(TenantContext database) => !PlatformDatabases.IsPlatformDatabase(database.DatabaseId)
        && (services.GetRequiredService<HomeLeases>().Find(database.DatabaseId!) is not { } lease || lease.Home == options.Region);

    private async Task<LocalDataRefusal?> RefusalAsync(TenantContext database, CancellationToken token) =>
        services.GetRequiredService<IDatabaseReplicas>().Holding(database) switch
        {
            NoReplica => null,
            PositionUnknown => new(HomeRefusal.PositionUnknown, "has a local replica whose position cannot be read, so nothing proves its primary file covers it"),
            HeldAt(var replica) => await services.GetRequiredService<IDatabaseRevisions>().CurrentRevisionAsync(database, token).ConfigureAwait(false) switch
            {
                null => new(HomeRefusal.PrimaryMissing, $"has a local replica at {replica} and no primary file"),
                { } primary when primary.Incarnation != replica.Incarnation || DatabaseRevision.Compare(replica, primary) > 0 =>
                    new(HomeRefusal.ReplicaNewer, $"has a local replica at {replica} that its primary file does not cover, at {primary}"),
                _ => null,
            },
        };

    private async Task<int> CatchUpAsync(IReadOnlyList<TenantContext> unheld, CancellationToken token)
    {
        if (unheld.Count == 0)
        {
            return 0;
        }

        var replication = services.GetRequiredService<RegionReplication>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(CatchUpBudget);
        var late = 0;
        await Parallel.ForEachAsync(unheld, new ParallelOptions { MaxDegreeOfParallelism = CatchUpParallelism, CancellationToken = token }, async (database, cancellation) =>
        {
            try
            {
                _ = await replication.CatchUpAsync(database, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _ = Interlocked.Increment(ref late);
            }
        }).ConfigureAwait(false);
        return late;
    }

    private void Fence(TenantContext database, LocalDataRefusal refusal, long epoch)
    {
        var id = database.DatabaseId!;
        var status = new FencedDatabase(id, HomeLeases.Reason(refusal.Kind), epoch, $"database {id} {refusal.Detail}");
        var known = _fenced.TryGetValue(id, out var existing) && existing.Status.Reason == status.Reason && existing.Status.Epoch == epoch;
        gate.Fence(id);
        _fenced[id] = new DatabaseFence(database, status);
        if (known)
        {
            return;
        }

        services.GetRequiredService<HomeLeases>().Refused(refusal.Kind, "database");
        logger.ZLogWarning($"Database fenced at regional writer epoch {epoch:@Epoch}: {status.Detail:@Detail}; it refuses writes while every other database of the region is served, and is checked again on each writer renewal");
    }

    private void Unfence(string database)
    {
        if (!_fenced.TryRemove(database, out var fence))
        {
            return;
        }

        gate.Unfence(database);
        logger.ZLogInformation($"Database fence lifted: database {database:@Database} was fenced at epoch {fence.Status.Epoch:@Epoch} for {fence.Status.Reason:@Reason} and its local data now holds every write its replica saw");
    }

    private void ClearFences()
    {
        foreach (var database in _fenced.Keys)
        {
            if (_fenced.TryRemove(database, out _))
            {
                gate.Unfence(database);
            }
        }
        _walkFenced.Clear();
    }
    private async Task<DrainReservation> BeginDrainAsync(INatsKVStore kv, string target, DrainProgress progress, CancellationToken token)
    {
        var entry = await ReadAsync(kv, options.Region, token).ConfigureAwait(false);
        if (entry is null)
        {
            return DrainRefused(progress, "regional owner unavailable");
        }
        var owner = Decode(entry.Value.Value!);
        var resuming = owner.Transfer is { Phase: Retiring } existing && existing.Source == options.MachineId
            && existing.SourceSession == _session && existing.Target == target;
        if (owner.Transfer is { } prior && !resuming)
        {
            if (prior.Phase != Retiring)
            {
                return DrainRefused(progress, "another regional transfer is still in progress");
            }
            if (await ReplacedSourcePendingAsync(prior, owner, progress, token).ConfigureAwait(false) is { } pending)
            {
                var label = PendingLabel(pending);
                LogSourceRetiring(logger, _lastRetiringStep == pending ? LogLevel.Debug : LogLevel.Information,
                    options.Region, prior.SourceSession, label);
                _lastRetiringStep = pending;
                return new Unavailable("the source is still retiring; waiting for " + label, "source_retiring");
            }
        }
        _lastRetiringStep = null;
        progress.Phase = DrainPhase.Readiness;
        progress.Peer = target;
        var candidate = await SelfRegionPeerAsync(target, token).ConfigureAwait(false);
        if (candidate is null
            || await StatusAsync(new Uri(candidate.InternalListener(options.InternalPort), "/v1/rollout/status"), token).ConfigureAwait(false)
                is not { Ready: true, Session.Length: > 0, Node: var candidateNode } || candidateNode != target)
        {
            return DrainRefused(progress, "the target has not passed readiness", "target_not_ready");
        }
        progress.Phase = DrainPhase.Reserve;
        progress.Peer = options.MachineId;
        if (resuming)
        {
            return entry.Value;
        }
        if (owner.Transfer is not null)
        {
            owner = owner with { Transfer = null };
            var cleared = await kv.UpdateAsync(options.Region, Encode(owner), entry.Value.Revision, cancellationToken: token).ConfigureAwait(false);
            entry = await ReadAsync(kv, options.Region, token).ConfigureAwait(false);
            if (entry?.Revision != cleared)
            {
                return DrainRefused(progress, "the regional owner changed while clearing retirement", "owner_changed");
            }
        }
        if (owner.Node != options.MachineId && owner.Node != target)
        {
            return DrainRefused(progress, "the regional owner changed before reservation", "owner_changed");
        }
        var transfer = new RegionalTransfer(Guid.NewGuid().ToString("N"), options.MachineId, target, _session, Preparing,
            DateTimeOffset.UtcNow + DrainBudget, owner.Epoch);
        var reserved = owner with { Transfer = transfer };
        var revision = await kv.UpdateAsync(options.Region, Encode(reserved), entry.Value.Revision, cancellationToken: token).ConfigureAwait(false);
        entry = await ReadAsync(kv, options.Region, token).ConfigureAwait(false);
        return entry?.Revision == revision ? entry.Value : DrainRefused(progress, "the stop reservation changed", "owner_changed");
    }

    private async Task<PendingStep?> ReplacedSourcePendingAsync(RegionalTransfer transfer, RegionalOwner owner, DrainProgress progress, CancellationToken token)
    {
        var pending = PendingStep.Readiness;
        progress.Phase = DrainPhase.Readiness;
        progress.Peer = transfer.Source;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(AttemptBudget);
        try
        {
            var source = await SelfRegionPeerAsync(transfer.Source, attempt.Token).ConfigureAwait(false);
            if (source is null)
            {
                return PendingStep.Restart;
            }
            var address = source.InternalListener(options.InternalPort);
            var status = await StatusAsync(new Uri(address, "/v1/rollout/status"), attempt.Token).ConfigureAwait(false);
            if (status is { Session: var session } && session == transfer.SourceSession)
            {
                return PendingStep.Restart;
            }
            if (status is not { Ready: true, Session.Length: > 0 } || status.Node != transfer.Source)
            {
                return pending;
            }
            pending = PendingStep.CatchUp;
            if (status.Owner != owner.Node || status.Epoch < owner.Epoch)
            {
                return pending;
            }
            progress.Phase = DrainPhase.Prime;
            using var primed = await _http.PostAsync(new Uri(address, "/v1/rollout/prime?source=" + Uri.EscapeDataString(owner.Node)), null, token).ConfigureAwait(false);
            return primed.IsSuccessStatusCode ? null : pending;
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var errorType = error.GetType().Name;
                var label = PendingLabel(pending);
                LogRetiringProbeFailed(logger, error, errorType, progress.Label, progress.Peer, label);
            }
            return pending;
        }
    }

    private static string PendingLabel(PendingStep pending) => pending switch
    {
        PendingStep.Restart => "restart with a different session",
        PendingStep.Readiness => "readiness",
        PendingStep.CatchUp => "catch-up to the current writer",
        _ => throw new UnreachableException()
    };

    [LoggerMessage(Message = "Regional source still retiring in {Region}, source session {SourceSession}: waiting for {Pending}")]
    private static partial void LogSourceRetiring(ILogger logger, LogLevel level, string region, string sourceSession, string pending);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Regional retirement probe failed with {ErrorType} during {Phase} with peer {Peer}; waiting for {Pending}")]
    private static partial void LogRetiringProbeFailed(ILogger logger, Exception exception, string errorType, string phase, string peer, string pending);
}
