// <copyright file="TbepClientInterleavingTests.cs" company="TerraScale">
// TerraBase (SE-14). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol.Tests;

using System.Net.Sockets;

/// <summary>
/// The unsolicited-notification interleaving contract of the C# client
/// (SE-14): frames a subscription streams while a call is in flight —
/// correlation 0, causation naming the subscription — are buffered by
/// the client and surface, in arrival order, from the next
/// <see cref="TbepClient.ReadFrameAsync"/>; they are never consumed and
/// discarded. A scripted engine over a real UDS pair drives the exact
/// interleaving: the commit's ordered batch and content-free trigger
/// (PD-2026-08-26-13 D3) arrive before the mutation's answer.
/// </summary>
public sealed class TbepClientInterleavingTests
{
    /// <summary>Subscribe, mutate, and receive the buffered batch plus trigger in arrival order.</summary>
    [Test]
    public async Task UnsolicitedFramesRacingACallSurfaceFromReadFrameAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tbep-client-{Guid.NewGuid():N}.sock");
        try
        {
            await using var engine = new ScriptedEngine(path, subscriptionId: 42);
            await using var client = TbepClient.Connect(path);

            await client.BindChannelAsync("backend", "tenant-a");
            var subscribe = await client.BackboneSubscribeAsync(42, new[] { "orders" }, fromSequence: null);
            await Assert.That(subscribe.SubscriptionId).IsEqualTo(42ul);

            // The scripted engine streams the batch and the trigger
            // before the mutation's answer: they race the in-flight call.
            var outcome = await client.SyncMutationSubmitAsync(
                1,
                "d-01",
                SyncChange.Put(new[] { new SyncField { Name = "qty", Value = TbepValue.Of(1L) } }),
                "op-01");
            await Assert.That(outcome.Outcome.Variant).IsEqualTo("Applied");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = await client.ReadFrameAsync(timeout.Token);
            await Assert.That(first.Kind).IsEqualTo(TbepRegistry.KindCode("SubscriptionBatch"));
            await Assert.That(first.Correlation).IsEqualTo(0ul);
            await Assert.That(first.Causation).IsEqualTo(42ul);
            var batch = TbepClient.DecodeBody<SubscriptionBatch>(first);
            await Assert.That(batch.Events.Single().OperationId).IsEqualTo("op-01");

            var second = await client.ReadFrameAsync(timeout.Token);
            await Assert.That(second.Kind).IsEqualTo(TbepRegistry.KindCode("TriggerNotification"));
            await Assert.That(second.Correlation).IsEqualTo(0ul);
            var trigger = TbepClient.DecodeBody<TriggerNotification>(second);
            await Assert.That(trigger.Sequence).IsEqualTo(1ul);
            await Assert.That(trigger.OperationId).IsEqualTo("op-01");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The scripted engine half: one accepted connection that answers the
    /// bind and the subscribe, then streams the commit's batch and
    /// content-free trigger before the mutation's answer — the exact
    /// interleaving the client must survive without losing a frame.
    /// </summary>
    private sealed class ScriptedEngine : IAsyncDisposable
    {
        private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource stopped = new();
        private readonly Task serving;

        public ScriptedEngine(string path, ulong subscriptionId)
        {
            this.listener.Bind(new UnixDomainSocketEndPoint(path));
            this.listener.Listen(1);
            this.serving = this.ServeAsync(subscriptionId, this.stopped.Token);
        }

        private async Task ServeAsync(ulong subscriptionId, CancellationToken cancellationToken)
        {
            using var socket = await this.listener.AcceptAsync(cancellationToken);
            using var stream = new NetworkStream(socket, ownsSocket: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await TbepWire.ReadFrame(stream);
                if (request is null)
                {
                    return;
                }

                switch (TbepRegistry.KindName(request.Kind))
                {
                    case "ChannelBindRequest":
                        await AnswerAsync(
                            stream,
                            "channel.bind",
                            request,
                            new ChannelBindResponse { Tenant = "tenant-a", ServingInstance = 1 }).ConfigureAwait(false);
                        break;
                    case "BackboneSubscribeRequest":
                        await AnswerAsync(
                            stream,
                            "backbone.subscribe",
                            request,
                            new BackboneSubscribeResponse { SubscriptionId = subscriptionId, StartSequence = 1 }).ConfigureAwait(false);
                        break;
                    case "SyncMutationSubmitRequest":
                        // The interleaving under test: the commit's batch
                        // and trigger race the in-flight call.
                        await TbepWire.WriteFrame(
                            stream,
                            Notification(
                                TbepRegistry.KindCode("SubscriptionBatch"),
                                subscriptionId,
                                new SubscriptionBatch
                                {
                                    SubscriptionId = subscriptionId,
                                    FromSequence = 0,
                                    Events = new[]
                                    {
                                        new ChangeEvent
                                        {
                                            Sequence = 1,
                                            OperationId = "op-01",
                                            Kind = ChangeKind.Put(),
                                            Collection = "orders",
                                            Document = "d-01",
                                        },
                                    },
                                    NextSequence = 2,
                                })).ConfigureAwait(false);
                        await TbepWire.WriteFrame(
                            stream,
                            Notification(
                                TbepRegistry.KindCode("TriggerNotification"),
                                subscriptionId,
                                new TriggerNotification
                                {
                                    SubscriptionId = subscriptionId,
                                    Collection = "orders",
                                    Sequence = 1,
                                    OperationId = "op-01",
                                })).ConfigureAwait(false);
                        await AnswerAsync(
                            stream,
                            "sync.mutation_submit",
                            request,
                            new SyncMutationSubmitResponse
                            {
                                SessionId = 1,
                                Outcome = MutationOutcome.Applied(new CommitReceipt
                                {
                                    Sequence = 1,
                                    Version = 1,
                                    OperationId = "op-01",
                                }),
                            }).ConfigureAwait(false);
                        return;
                    default:
                        throw new InvalidOperationException($"the script does not serve {TbepRegistry.KindName(request.Kind)}");
                }
            }
        }

        private static async Task AnswerAsync<T>(Stream stream, string operation, TbepWire.Frame request, T body)
        {
            var bytes = MessagePackSerializer.Serialize(body, TbepOptions.Strict);
            await TbepWire.WriteFrame(
                stream,
                TbepWire.Seal(TbepRegistry.ResponseKindOf(operation), correlation: 1, request.Correlation, idempotency: null, bytes)).ConfigureAwait(false);
        }

        private static TbepWire.Frame Notification<T>(ushort kind, ulong subscriptionId, T body)
        {
            var bytes = MessagePackSerializer.Serialize(body, TbepOptions.Strict);
            return TbepWire.Seal(kind, correlation: 0, subscriptionId, idempotency: null, bytes);
        }

        public async ValueTask DisposeAsync()
        {
            this.stopped.Cancel();
            await this.serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            this.listener.Dispose();
            this.stopped.Dispose();
        }
    }
}
