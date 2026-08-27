// <copyright file="TbepChannelRoutingTests.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol.Tests;

using System.Net.Sockets;

/// <summary>
/// The error-routing contract of the serving channel (SE-08 repair): an
/// error frame the engine answers for a call the caller already
/// abandoned (canceled) names a correlation nobody tracks — it completes
/// NOTHING. The oldest-pending heuristic applies only to a true
/// decode-level refusal, whose error frame names no causation (the
/// refused frame's identity is unreadable by construction); a live call
/// must never receive another call's error.
/// </summary>
public sealed class TbepChannelRoutingTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"tbep-routing-{Guid.NewGuid():N}.sock");

    /// <summary>An error for an abandoned call completes nothing: the pending call gets its own answer, never that error.</summary>
    [Test]
    public async Task AnErrorForAnAbandonedCallCompletesNothing()
    {
        var path = SocketPath();
        try
        {
            await using var engine = new RacingEngine(path, decodeRefusal: false);
            await using var channel = TbepChannel.Connect(path);
            await channel.BindChannelAsync("backend", "tenant-a");

            // The first call is abandoned by its caller while the engine
            // still owes it an answer.
            using var cancel = new CancellationTokenSource();
            var abandoned = channel.DocumentReadAsync("orders", "abandoned", cancel.Token);
            await engine.SawFirstRequest.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            try
            {
                await abandoned;
            }
            catch (OperationCanceledException)
            {
                // The caller gave up; the engine's late answer is the
                // engine's to waste.
            }

            // A second, live call is pending when the abandoned call's
            // error finally arrives.
            var survivor = channel.DocumentReadAsync("orders", "survivor");
            await engine.SawSecondRequest.WaitAsync(TimeSpan.FromSeconds(10));
            engine.Release();

            var answer = await survivor.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(answer.Found).IsTrue();
            await Assert.That(answer.Document!.Id).IsEqualTo("survivor");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A decode-level refusal names no causation and fails the oldest still-pending call — the heuristic's only trigger.</summary>
    [Test]
    public async Task ADecodeRefusalWithoutCausationFailsTheOldestPendingCall()
    {
        var path = SocketPath();
        try
        {
            await using var engine = new RacingEngine(path, decodeRefusal: true);
            await using var channel = TbepChannel.Connect(path);
            await channel.BindChannelAsync("backend", "tenant-a");

            // The engine could not decode the frame: its error names no
            // causation, and the oldest still-pending call (the refused
            // one, in arrival order) surfaces the typed refusal instead
            // of hanging — the connection stays usable.
            var refused = channel.DocumentReadAsync("orders", "d-01");
            var failure = await Assert.ThrowsAsync<TbepException>(() => refused);
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Code!.Variant).IsEqualTo("MalformedFrame");

            var answer = await channel.DocumentReadAsync("orders", "d-02");
            await Assert.That(answer.Found).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The scripted engine half: answers the bind, then choreographs two
    /// document reads — in the abandoned-call row it holds both answers
    /// until released, then answers the ABANDONED call with an error
    /// naming its causation before answering the live call; in the
    /// decode-refusal row it answers the first read with a causation-less
    /// error immediately.
    /// </summary>
    private sealed class RacingEngine : IAsyncDisposable
    {
        private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource stopped = new();
        private readonly TaskCompletionSource sawFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource sawSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task serving;

        public RacingEngine(string path, bool decodeRefusal)
        {
            this.listener.Bind(new UnixDomainSocketEndPoint(path));
            this.listener.Listen(1);
            this.serving = this.ServeAsync(decodeRefusal, this.stopped.Token);
        }

        /// <summary>Completes when the engine received the first read.</summary>
        public Task SawFirstRequest => this.sawFirst.Task;

        /// <summary>Completes when the engine received the second read.</summary>
        public Task SawSecondRequest => this.sawSecond.Task;

        /// <summary>Lets the engine answer: the abandoned call's error first, then the live call's answer.</summary>
        public void Release() => this.release.TrySetResult();

        private async Task ServeAsync(bool decodeRefusal, CancellationToken cancellationToken)
        {
            using var socket = await this.listener.AcceptAsync(cancellationToken);
            using var stream = new NetworkStream(socket, ownsSocket: true);
            TbepWire.Frame? first = null;
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
                        await this.AnswerAsync(
                            stream,
                            "channel.bind",
                            request,
                            new ChannelBindResponse { Tenant = "tenant-a", ServingInstance = 1 }).ConfigureAwait(false);
                        break;
                    case "DocumentReadRequest":
                        var body = MessagePackSerializer.Deserialize<DocumentReadRequest>(request.Body, TbepOptions.Strict);
                        if (decodeRefusal)
                        {
                            if (first is null)
                            {
                                first = request;
                                this.sawFirst.TrySetResult();
                                await this.ErrorAsync(stream, causation: 0, correlation: 1, ErrorCode.MalformedFrame(), "the frame does not decode").ConfigureAwait(false);
                            }
                            else
                            {
                                await this.ReadAnswerAsync(stream, request, body).ConfigureAwait(false);
                            }

                            break;
                        }

                        if (first is null)
                        {
                            // The abandoned call: held, not answered.
                            first = request;
                            this.sawFirst.TrySetResult();
                            break;
                        }

                        this.sawSecond.TrySetResult();
                        await this.release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                        // The engine answers the abandoned call with an
                        // error NAMING its causation, then the live call
                        // with its real answer — in that order.
                        await this.ErrorAsync(stream, causation: first.Correlation, correlation: 1, ErrorCode.EngineFailure(), "the abandoned call failed on the engine").ConfigureAwait(false);
                        await this.ReadAnswerAsync(stream, request, body).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException($"the script does not serve {TbepRegistry.KindName(request.Kind)}");
                }
            }
        }

        private async Task ReadAnswerAsync(Stream stream, TbepWire.Frame request, DocumentReadRequest body) =>
            await this.AnswerAsync(
                stream,
                "document.read",
                request,
                new DocumentReadResponse
                {
                    Found = true,
                    Document = new DocumentBody
                    {
                        Id = body.Document,
                        Collection = body.Collection,
                        Version = 1,
                        Body = TbepValue.Of($"body-of-{body.Document}"),
                    },
                }).ConfigureAwait(false);

        private async Task AnswerAsync<T>(Stream stream, string operation, TbepWire.Frame request, T body)
        {
            var bytes = MessagePackSerializer.Serialize(body, TbepOptions.Strict);
            await TbepWire.WriteFrame(
                stream,
                TbepWire.Seal(TbepRegistry.ResponseKindOf(operation), correlation: 1, request.Correlation, idempotency: null, bytes)).ConfigureAwait(false);
        }

        private async Task ErrorAsync(Stream stream, ulong causation, ulong correlation, ErrorCode code, string detail)
        {
            var bytes = MessagePackSerializer.Serialize(new Error { Code = code, Detail = detail }, TbepOptions.Strict);
            await TbepWire.WriteFrame(
                stream,
                TbepWire.Seal(TbepRegistry.KindCode("Error"), correlation, causation, idempotency: null, bytes)).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            this.stopped.Cancel();
            await this.serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            this.listener.Dispose();
            this.stopped.Dispose();
        }
    }
}
