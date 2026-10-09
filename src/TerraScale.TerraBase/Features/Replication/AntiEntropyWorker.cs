using System.Text;
using Google.Protobuf;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

// One maintenance exchange at a time; it cannot consume normal replication's credits.
internal sealed partial class AntiEntropyWorker(
    IStorageEngine engine, ITenantDirectory tenants, IMembership membership, IAntiEntropyState state,
    PeerCredentials credentials, TerraBaseOptions options, ILogger<AntiEntropyWorker> logger, TimeProvider time, IMemoryBudget memory,
    IUsageMeter usage) : BackgroundService
{
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ExchangeTimeout = TimeSpan.FromMinutes(15);

    // Each peer address's region from its last answered digest: an unanswered one is still charged there. Rebuilt each pass
    // from the current addresses, so bounded by the membership.
    private Dictionary<Uri, string> _regions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    LogRepairFailed(logger, error);
                }

                // A slow pass must still leave an idle gap; unchanged journals cannot reveal physical divergence.
                await Task.Delay(ReconcileInterval, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // gRPC cancellation may surface as RpcException during shutdown.
        }
    }

    private async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        var view = await membership.RefreshAsync(stoppingToken).ConfigureAwait(false);
        var addresses = new HashSet<Uri>(options.PeerAddresses);
        foreach (var member in view.Members)
        {
            if (member.Name == membership.Self.Name || string.IsNullOrEmpty(member.PeerAddress))
            {
                continue;
            }

            if (TerraBaseOptions.TryParsePeerAddress(member.PeerAddress, out var address))
            {
                _ = addresses.Add(address);
            }
            else
            {
                LogInvalidPeer(logger);
            }
        }

        var regions = new Dictionary<Uri, string>();
        foreach (var address in addresses)
        {
            if (_regions.TryGetValue(address, out var known))
            {
                regions[address] = known;
            }
        }

        _regions = regions;
        foreach (var address in addresses)
        {
            try
            {
                using var lease = credentials.Capture();
                using var channel = PeerChannel.Create(address, lease);
                var client = new Alpha.AntiEntropy.AntiEntropyClient(channel);
                await foreach (var database in engine.ListDatabasesAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        using var progress = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout, time);
                        // Advancing pages cannot indefinitely monopolize maintenance for the remaining databases.
                        using var exchange = new CancellationTokenSource(ExchangeTimeout, time);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, progress.Token, exchange.Token);
                        if (await tenants.ReplicatesAsync(database, deadline.Token).ConfigureAwait(false) is not true)
                        {
                            continue;
                        }

                        if (await RepairAsync(client, database, progress, _regions.GetValueOrDefault(address), deadline.Token).ConfigureAwait(false) is { } region)
                        {
                            _regions[address] = region;
                        }
                    }
                    catch (RpcException error) when (error.StatusCode == StatusCode.Aborted && !stoppingToken.IsCancellationRequested)
                    {
                        LogRepairDeferred(logger);
                    }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    {
                        LogRepairFailed(logger, error);
                    }
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogRepairFailed(logger, error);
            }
        }
    }

    // Returns the peer's region once its digest named it.
    private async Task<string?> RepairAsync(Alpha.AntiEntropy.AntiEntropyClient client, TenantContext database,
        CancellationTokenSource progress, string? knownRegion, CancellationToken cancellationToken)
    {
        using var reservation = AntiEntropyState.Reserve(memory);
        var identity = new ShardIdentity
        {
            Organization = database.OrganizationId,
            Project = database.ProjectId!,
            Database = database.DatabaseId!,
            Region = options.Region,
        };
        progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
        var store = await state.OpenAsync(identity, cancellationToken).ConfigureAwait(false);
        progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
        var read = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            identity.Origin = ByteString.CopyFrom((await read.GetOriginAsync(cancellationToken).ConfigureAwait(false)).Span);
        }

        progress.CancelAfter(AntiEntropyState.DigestTimeout);
        var local = await state.DigestAsync(store, cancellationToken).ConfigureAwait(false);
        progress.CancelAfter(AntiEntropyState.DigestTimeout);
        using var digestCall = client.DigestAsync(identity, cancellationToken: cancellationToken);
        MerkleDigest remote;
        try
        {
            remote = await digestCall.ResponseAsync.ConfigureAwait(false);
        }
        catch when (knownRegion is not null)
        {
            // Delivered but unanswered: charged to the region this peer named before, as a replication Hello is.
            Metered(database, knownRegion, identity, sent: true);
            throw;
        }

        // Anti-entropy traffic is metered once the digest names the peer's region, like a replication Hello.
        var peerRegion = remote.Region;
        Metered(database, peerRegion, identity, sent: true);
        Metered(database, peerRegion, remote, sent: false);
        if (remote.Incarnation.Length != 16 || remote.Origin.Length != 16 || remote.Envelope.Length > 1024
            || remote.AppliedSource is not { } applied || !applied.Origin.Equals(identity.Origin)
            || applied.Sequence > long.MaxValue || applied.Digest.Length != JournalDigest.Length)
        {
            throw new InvalidDataException("invalid peer Merkle digest");
        }

        var ranges = MerkleTree.Mismatches(local, ApplicationEnvelope.Decode(remote.Envelope.Span, MerkleTree.DigestBytes));
        if (ranges == 0)
        {
            return peerRegion;
        }

        identity.Incarnation = remote.Incarnation;
        progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
        var proofRead = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (proofRead.ConfigureAwait(false))
        {
            var proof = await proofRead.GetChangeDigestAsync((long)applied.Sequence, cancellationToken).ConfigureAwait(false);
            if (proof is null || !proof.Value.Span.SequenceEqual(applied.Digest.Span))
            {
                throw new InvalidDataException("the peer's repair frontier requires journal restore recovery");
            }

            identity.SourceProof = applied;
            var (sequence, digest) = await proofRead.GetAppliedFrontierAsync(remote.Origin.Memory, cancellationToken).ConfigureAwait(false);
            identity.TargetProof = new JournalProof { Origin = remote.Origin, Sequence = (ulong)sequence, Digest = ByteString.CopyFrom(digest.Span) };
        }

        var receiving = identity.Clone();
        receiving.Incarnation = ByteString.CopyFrom(store.Incarnation.ToByteArray(bigEndian: true));
        receiving.Origin = remote.Origin;
        (receiving.SourceProof, receiving.TargetProof) = (identity.TargetProof, identity.SourceProof);
        var request = new RangeRequest { Identity = identity, Ranges = ranges };
        // Pull before pushing: only the winning joined states need to travel back.
        while (true)
        {
            progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
            // A request counts as sent even when its response fails.
            Metered(database, peerRegion, request, sent: true);
            using var readCall = client.ReadRangeAsync(request, cancellationToken: cancellationToken);
            var page = await readCall.ResponseAsync.ConfigureAwait(false);
            Metered(database, peerRegion, page, sent: false);
            CheckPage(page, request);
            if (!page.Envelope.IsEmpty)
            {
                progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
                _ = await state.MergeAsync(store, receiving, page.Envelope, cancellationToken).ConfigureAwait(false);
            }

            if (!page.HasMore)
            {
                break;
            }

            request.AfterCollection = page.AfterCollection;
            request.AfterId = page.AfterId;
        }

        request.Identity = receiving;
        request.AfterCollection = string.Empty;
        request.AfterId = string.Empty;
        while (true)
        {
            progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
            var page = await state.ReadAsync(store, request, cancellationToken).ConfigureAwait(false);
            if (!page.Envelope.IsEmpty)
            {
                progress.CancelAfter(ReplicationProtocol.ProgressTimeout);
                var merge = new StateRequest { Identity = identity, Envelope = page.Envelope };
                Metered(database, peerRegion, merge, sent: true);
                using var mergeCall = client.MergeAsync(merge, cancellationToken: cancellationToken);
                Metered(database, peerRegion, await mergeCall.ResponseAsync.ConfigureAwait(false), sent: false);
            }

            if (!page.HasMore)
            {
                break;
            }

            request.AfterCollection = page.AfterCollection;
            request.AfterId = page.AfterId;
        }

        return peerRegion;
    }

    // One message on the wire: its encoded bytes plus the five-byte gRPC message prefix.
    private void Metered(TenantContext database, string peerRegion, IMessage message, bool sent) =>
        usage.RecordReplicationBytes(database, peerRegion, "anti_entropy", sent, message.CalculateSize() + 5L);

    private static void CheckPage(StatePage page, RangeRequest request)
    {
        // Scan cursors can include excluded legacy keys; SQLite orders both parts by UTF-8 bytes.
        var collectionOrder = Encoding.UTF8.GetBytes(page.AfterCollection).AsSpan()
            .SequenceCompareTo(Encoding.UTF8.GetBytes(request.AfterCollection));
        if (page.Envelope.Length > ReplicationProtocol.MaxApplicationBytes
            || !AntiEntropyState.ValidCursor(page.AfterCollection, page.AfterId)
            || (page.HasMore && (page.AfterCollection.Length == 0 || page.AfterId.Length == 0
                || collectionOrder < 0 || (collectionOrder == 0
                    && Encoding.UTF8.GetBytes(page.AfterId).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(request.AfterId)) <= 0))))
        {
            throw new InvalidDataException("invalid peer repair page or continuation");
        }
    }

    [LoggerMessage(LogLevel.Warning, "Anti-entropy repair failed; retrying on the next reconciliation")]
    private static partial void LogRepairFailed(ILogger logger, Exception error);

    [LoggerMessage(LogLevel.Debug, "Anti-entropy journal frontier advanced; retrying on the next reconciliation")]
    private static partial void LogRepairDeferred(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Skipping an invalid anti-entropy member peer address")]
    private static partial void LogInvalidPeer(ILogger logger);
}
