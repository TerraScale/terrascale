using System.Globalization;
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

internal sealed record RegionalWriter(string Node, long Epoch, string Session);

/// <summary>
/// A separate, exclusive admission lease survives an owner CAS. A new owner
/// cannot commit while the former owner's admission might still be valid.
/// All calls are serialized by RegionalHandoff's change gate.
/// </summary>
internal sealed class RegionalWriterLease(NatsJSContext js, INatsKVStore kv, string region, string node, string session)
{
    internal const string Bucket = "terrabase-region-writers";
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(1);
    private ulong? _revision;
    private RegionalWriter? _mine;

    internal async Task<long?> RenewAsync(long epoch, CancellationToken cancellationToken)
    {
        var writer = new RegionalWriter(node, epoch, session);
        var sent = TimeProvider.System.GetTimestamp();
        if (_mine != writer)
        {
            _revision = null;
            _mine = writer;
        }

        var value = JsonSerializer.SerializeToUtf8Bytes(writer, TerraBaseJsonContext.Default.RegionalWriter);
        try
        {
            if (_revision is { } revision)
            {
                var headers = new NatsHeaders { ["Nats-TTL"] = ((long)Ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s" };
                var answer = await js.PublishAsync($"$KV.{Bucket}.{region}", value, headers: headers,
                    opts: new NatsJSPubOpts { ExpectedLastSubjectSequence = revision }, cancellationToken: cancellationToken).ConfigureAwait(false);
                answer.EnsureSuccess();
                _revision = answer.Seq;
            }
            else
            {
                // Create must fail while any previous writer still holds the key,
                // including the same node's previous process or previous epoch.
                _revision = await kv.CreateAsync(region, value, Ttl, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is NatsKVCreateException or NatsKVWrongLastRevisionException
            || error is NatsJSApiException { Error.ErrCode: 10071 })
        {
            _revision = null;
            return null;
        }

        // Measure from send, not reply: a delayed response never extends local
        // admission beyond the server's TTL. This uses a monotonic clock.
        var until = sent + (long)((Ttl - Margin).TotalSeconds * TimeProvider.System.TimestampFrequency);
        return TimeProvider.System.GetTimestamp() < until ? until : null;
    }

    internal async Task ReleaseDrainedAsync(CancellationToken cancellationToken)
    {
        if (_revision is not { } revision)
        {
            return;
        }
        try
        {
            await kv.DeleteAsync(region, new NatsKVDeleteOpts { Revision = revision }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NatsKVKeyNotFoundException or NatsKVKeyDeletedException or NatsKVWrongLastRevisionException)
        {
            // Expiry or another CAS already fenced this exact lease. Never
            // delete a successor's lease, even if a release response was lost.
        }
        finally
        {
            _revision = null;
        }
    }
}
