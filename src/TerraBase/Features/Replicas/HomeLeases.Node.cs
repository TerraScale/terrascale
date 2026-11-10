using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using System.Globalization;
using System.Text.Json;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal sealed partial class HomeLeases
{
    private async Task RenewAsync(NatsJSContext js, INatsKVStore kv, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RenewEvery, leaseGate.Time);
        do
        {
            var sent = leaseGate.Time.GetTimestamp();
            try
            {
                var lease = JsonSerializer.SerializeToUtf8Bytes(new NodeLease(options.MachineId, options.Region, _session), ReplicasJsonContext.Default.NodeLease);
                var fresh = _nodeRevision is null;
                _nodeRevision = _nodeRevision is { } revision
                    ? await PublishNodeAsync(js, lease, revision, cancellationToken).ConfigureAwait(false)
                    : await TakeNodeAsync(js, kv, lease, cancellationToken).ConfigureAwait(false);
                leaseGate.Renewed(sent + (long)((Ttl - Margin).TotalSeconds * leaseGate.Time.TimestampFrequency));
                if (fresh)
                {
                    logger.ZLogInformation($"This node holds a new home lease session");
                    await ConfirmAllAsync(kv, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (NatsJSApiException error) when (error.Error.ErrCode == WrongLastSequence)
            {
                Lapse();
                logger.ZLogWarning($"This node's home lease expired; its databases with replicas refuse writes until it confirms each lease again");
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"The home lease renewal failed; it retries: {error.GetType().Name:@Error}");
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private void Lapse()
    {
        _nodeRevision = null;
        _session = Ulid.NewUlid().ToString();
        foreach (var database in leaseGate.Leased)
        {
            leaseGate.Lease(database, held: false);
        }
    }

    private async Task<ulong> TakeNodeAsync(NatsJSContext js, INatsKVStore kv, byte[] lease, CancellationToken cancellationToken)
    {
        try
        {
            return await kv.CreateAsync(NodeKey, lease, Ttl, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (NatsKVCreateException)
        {
            var current = await kv.GetEntryAsync<byte[]>(NodeKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await PublishNodeAsync(js, lease, current.Revision, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ulong> PublishNodeAsync(NatsJSContext js, byte[] lease, ulong revision, CancellationToken cancellationToken)
    {
        var headers = new NatsHeaders { ["Nats-TTL"] = ((long)Ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s" };
        var answer = await js.PublishAsync($"$KV.{Bucket}.{NodeKey}", lease, headers: headers,
            opts: new NatsJSPubOpts { ExpectedLastSubjectSequence = revision }, cancellationToken: cancellationToken).ConfigureAwait(false);
        answer.EnsureSuccess();
        return answer.Seq;
    }

}
