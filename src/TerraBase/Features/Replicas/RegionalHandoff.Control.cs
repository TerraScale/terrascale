using NATS.Client.KeyValueStore;
using System.Text.Json;
using TerraBase.Features.Replicas;
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
    private RegionalOwner? _startupOwner;
    private bool _protocolPublished;
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(30);
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
            owners.Renew(until);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            logger.ZLogWarning($"Regional activation attempt exceeded its bound; admission stays fenced and recovery retries");
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
                Version: ProtocolVersion,
                Prepared: new RegionalPrepared(owner.Node, transfer.Target, owner.Epoch, transfer.Id));
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
            foreach (var database in _promoted.Values)
            {
                services.GetRequiredService<RegionReplication>().Resume(database);
            }
            _promoted.Clear();
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

    private async Task<bool> HasPreparedAsync(INatsKVStore kv, RegionalOwner owner, CancellationToken token)
    {
        var entry = await ReadAsync(kv, PreparedKey(owner.Node), token).ConfigureAwait(false);
        var prepared = owner.Prepared ?? (entry?.Value is { } bytes
            ? JsonSerializer.Deserialize(bytes, ReplicasJsonContext.Default.RegionalPrepared) : null);
        var source = owner.Previous ?? (owner.Version == 0 ? owner.Node : null);
        return prepared is not null && prepared.Target == owner.Node && prepared.Source == source && prepared.Epoch == owner.Epoch - 1
            && (owner.Transfer is not { Phase: Activating } transfer || transfer.Id == prepared.Operation);
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
        if (await HasPreparedAsync(kv, owner, token).ConfigureAwait(false))
        {
            return true;
        }
        if (owner.Version == 0 && _startupOwner == owner
            && await ReadAsync(kv, "protocol." + options.Region, token).ConfigureAwait(false) is null)
        {
            await WritePreparedAsync(kv, new RegionalPrepared(owner.Previous ?? owner.Node, owner.Node, owner.Epoch - 1, null), token).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task<NatsKVEntry<byte[]>?> BeginDrainAsync(INatsKVStore kv, string target, CancellationToken token)
    {
        var candidate = await SelfRegionPeerAsync(target, token).ConfigureAwait(false);
        if (candidate is null
            || await StatusAsync(new Uri(candidate.InternalListener(options.InternalPort), "/v1/rollout/status"), token).ConfigureAwait(false)
                is not { Ready: true, Session.Length: > 0, Node: var candidateNode } || candidateNode != target)
        {
            return null;
        }
        var entry = await ReadAsync(kv, options.Region, token).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }
        var owner = Decode(entry.Value.Value!);
        if (owner.Transfer is { } prior)
        {
            if (prior is { Phase: Retiring } && prior.Source == options.MachineId && prior.SourceSession == _session && prior.Target == target)
            {
                return entry;
            }
            if (prior.Phase != Retiring || !await ReplacedSourceReadyAsync(prior, owner, token).ConfigureAwait(false))
            {
                return null;
            }
            owner = owner with { Transfer = null };
            var cleared = await kv.UpdateAsync(options.Region, Encode(owner), entry.Value.Revision, cancellationToken: token).ConfigureAwait(false);
            entry = await ReadAsync(kv, options.Region, token).ConfigureAwait(false);
            if (entry?.Revision != cleared)
            {
                return null;
            }
        }
        if (owner.Node != options.MachineId && owner.Node != target)
        {
            return null;
        }
        var transfer = new RegionalTransfer(Guid.NewGuid().ToString("N"), options.MachineId, target, _session, Preparing,
            DateTimeOffset.UtcNow + DrainBudget, owner.Epoch);
        var reserved = owner with { Transfer = transfer };
        var revision = await kv.UpdateAsync(options.Region, Encode(reserved), entry.Value.Revision, cancellationToken: token).ConfigureAwait(false);
        entry = await ReadAsync(kv, options.Region, token).ConfigureAwait(false);
        return entry?.Revision == revision ? entry : null;
    }

    private async Task<bool> ReplacedSourceReadyAsync(RegionalTransfer transfer, RegionalOwner owner, CancellationToken token)
    {
        var source = await SelfRegionPeerAsync(transfer.Source, token).ConfigureAwait(false);
        if (source is null)
        {
            return false;
        }
        var address = source.InternalListener(options.InternalPort);
        if (await StatusAsync(new Uri(address, "/v1/rollout/status"), token).ConfigureAwait(false)
            is not { Ready: true, Session.Length: > 0 } status || status.Node != transfer.Source
            || status.Session == transfer.SourceSession || status.Owner != owner.Node || status.Epoch < owner.Epoch)
        {
            return false;
        }
        using var primed = await _http.PostAsync(new Uri(address, "/v1/rollout/prime?source=" + Uri.EscapeDataString(owner.Node)), null, token).ConfigureAwait(false);
        return primed.IsSuccessStatusCode;
    }
}
