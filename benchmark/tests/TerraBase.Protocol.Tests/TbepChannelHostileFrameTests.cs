// <copyright file="TbepChannelHostileFrameTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol.Tests;

using System.Net.Sockets;

/// <summary>Hostile TBEP input must fail closed at both wire and channel boundaries.</summary>
public sealed class TbepChannelHostileFrameTests
{
    /// <summary>A malformed MessagePack envelope is always reported as a wire failure.</summary>
    [Test]
    public async Task MalformedEnvelopeIsNormalizedAsWireFailure()
    {
        var failure = await Assert.That(() => TbepWire.Decode(new byte[] { 0x91 })).Throws<TbepWire.WireException>();

        await Assert.That(failure!.Failure).IsEqualTo(TbepWire.WireFailure.MalformedFrame);
    }

    /// <summary>A malformed notification body fails all pending calls and closes the channel.</summary>
    [Test]
    public async Task MalformedNotificationFailsPendingCallAndClosesChannel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tbep-hostile-{Guid.NewGuid():N}.sock");
        try
        {
            await using var engine = new HostileEngine(path);
            await using var channel = TbepChannel.Connect(path);
            await channel.BindChannelAsync("backend", "tenant-a");
            await channel.BackboneSubscribeAsync(42, collections: null, fromSequence: null, new NoopSink());

            var pending = channel.DocumentReadAsync("orders", "hostile");
            await engine.SawReadRequest.WaitAsync(TimeSpan.FromSeconds(10));

            var failure = await Assert.ThrowsAsync<TbepException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            await Assert.That(failure).IsNotNull();
            await Assert.That(channel.IsClosed).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class NoopSink : TbepChannel.ITbepNotificationSink
    {
        public void OnBatch(SubscriptionBatch batch)
        {
        }

        public void OnTrigger(TriggerNotification trigger)
        {
        }

        public void OnClosed(SubscriptionClosed closed)
        {
        }
    }

    private sealed class HostileEngine : IAsyncDisposable
    {
        private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource stopped = new();
        private readonly TaskCompletionSource sawReadRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task serving;

        public HostileEngine(string path)
        {
            this.listener.Bind(new UnixDomainSocketEndPoint(path));
            this.listener.Listen(1);
            this.serving = this.ServeAsync(this.stopped.Token);
        }

        public Task SawReadRequest => this.sawReadRequest.Task;

        private async Task ServeAsync(CancellationToken cancellationToken)
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
                            new BackboneSubscribeResponse { SubscriptionId = 42, StartSequence = 1 }).ConfigureAwait(false);
                        break;
                    case "DocumentReadRequest":
                        this.sawReadRequest.TrySetResult();
                        var hostile = TbepWire.Seal(
                            TbepRegistry.KindCode("SubscriptionBatch"),
                            correlation: 0,
                            causation: 42,
                            idempotency: null,
                            body: new byte[] { 0xc1 });
                        await TbepWire.WriteFrame(stream, hostile).ConfigureAwait(false);
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

        public async ValueTask DisposeAsync()
        {
            this.stopped.Cancel();
            await this.serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            this.listener.Dispose();
            this.stopped.Dispose();
        }
    }
}
