using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeResponseBoundaryTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PostDispatchAdmissionExceptionsPreserveActualResponseAndUncertainIdentity(bool bodyFailure)
    {
        var calls = 0;
        using var handler = new NativeClientTests.Handler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            if (!bodyFailure) throw new NativeAdmissionException("discovery_forbidden", 403);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new AdmissionFailureContent() };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });
        using var client = NativeClientTests.Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        var result = await client.CommitAsync(prepared);
        var failure = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
        await Assert.That(failure.Code).IsEqualTo("invalid_response");
        await Assert.That(failure.Status).IsEqualTo(bodyFailure ? 200 : (int?)null);
        await Assert.That(failure.RetryAfter).IsEqualTo(bodyFailure ? TimeSpan.FromSeconds(7) : (TimeSpan?)null);
        await Assert.That(failure.Transaction).IsEqualTo(prepared.Identity);
        await Assert.That(failure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LargeDecodeRemainsInsideCallerAndDeadlineIsolation(bool cancelCaller)
    {
        var context = Enumerable.Range(1, 10_000).Select(index =>
            new NativeObserved($"{index:x8}-0000-7000-8000-000000000005", new string('1', 32), 1)).ToImmutableArray();
        var point = new NativePoint(NativePointState.Deleted, new string('d', 64), context,
            [new NativeSibling(new NativeEvent(NativeCodecTests.Database, 0, context[0].Replica, context[0].Incarnation, 1),
                1, NativeCodecTests.SchemaDigest, new NativeWriteValue.Tombstone())]);
        var body = NativeCodec.EncodePoint(point);
        using var handler = new NativeClientTests.Handler((_, _) => Task.FromResult(NativeClientTests.Json(body)));
        var clock = new NativeDeadlineClock();
        using var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
            _ => ValueTask.FromResult("tbk1.fixture-key"), TimeSpan.FromSeconds(30), handler, clock);
        using var caller = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<NativePoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Pause a decoder partway through a genuine large response. The JSON
        // document must remain owned by the worker after the caller returns.
        var pending = client.RequestAsync("/fixture", HttpMethod.Get, null, root =>
        {
            _ = root.GetProperty("context")[0].GetProperty("replica");
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Decoder was not released.");
            var decoded = NativeCodec.DecodePoint(root);
            completed.TrySetResult(decoded);
            return decoded;
        }, ["read"], caller.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancelCaller) caller.Cancel();
            else clock.Expire();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code)
                .IsEqualTo(cancelCaller ? "cancelled" : "deadline_exceeded");
            await Assert.That(completed.Task.IsCompleted).IsFalse();
        }
        finally { release.Set(); }
        var late = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(late.Context.Length).IsEqualTo(context.Length);
    }

    [Test]
    [Arguments("credential")]
    [Arguments("admission")]
    [Arguments("assert")]
    public async Task UnrelatedProviderCancellationPropagates(string stage)
    {
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected."));
        using var client = new NativeClient(NativeCodecTests.Scope, new CancellingProvider(stage, unrelated.Token),
            _ => stage == "credential" ? throw new OperationCanceledException(unrelated.Token) : ValueTask.FromResult("tbk1.fixture-key"),
            TimeSpan.FromSeconds(10), handler);
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document));
        await Assert.That(failure!.CancellationToken).IsEqualTo(unrelated.Token);
    }

    [Test]
    public async Task CallerOwnedHttpClientTimeoutIsAnIntentionalShorterDeadline()
    {
        using var handler = new NativeClientTests.Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("No response expected.");
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var client = new NativeClient(http, NativeCodecTests.Scope, "https://gru.api.terrascale.tech",
            _ => ValueTask.FromResult("tbk1.fixture-key"), TimeSpan.FromSeconds(10));
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        var result = await client.CommitAsync(prepared).WaitAsync(TimeSpan.FromSeconds(5));
        var error = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
        await Assert.That(error.Code).IsEqualTo("deadline_exceeded");
        await Assert.That(error.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        await Assert.That(error.Transaction).IsEqualTo(prepared.Identity);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task UndeclaredSerializingContentStopsAtTheBodyLimit(bool discovery, bool derivedStreamContent)
    {
        var limit = discovery ? 64 * 1024 : NativeCodec.MaximumBodyBytes;
        var serializer = new OversizedSerializer();
        using HttpContent content = derivedStreamContent ? new DerivedOversizedContent(serializer) : new OversizedContent(serializer);
        using var handler = new NativeClientTests.Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        if (discovery)
        {
            using var router = new NativeDiscoveryRouter(TimeSpan.FromSeconds(5), handler);
            var failure = await Assert.ThrowsAsync<NativeAdmissionException>(() => router.ResolveAsync(
                NativeCodecTests.Scope with { Shard = 1 }, "tbk1.AAAAAAAAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                ["read"], CancellationToken.None).AsTask());
            await Assert.That(failure!.Code).IsEqualTo("invalid_discovery");
        }
        else
        {
            using var client = NativeClientTests.Client(handler);
            var result = await client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document);
            await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("invalid_response");
        }
        await Assert.That(serializer.BytesWritten).IsEqualTo(limit);
        await Assert.That(serializer.AttemptedBytes).IsEqualTo(limit + 8192);
    }

    [Test]
    [Arguments("point")]
    [Arguments("query")]
    [Arguments("receipt")]
    [Arguments("status")]
    [Arguments("error")]
    [Arguments("commit")]
    public async Task PublicDecodersEnforceTheBodyBudgetForExternallyParsedJson(string decoder)
    {
        using var external = JsonDocument.Parse("{\"padding\":\"" + new string('a', NativeCodec.MaximumBodyBytes) + "\"}");
        var json = external.RootElement.GetRawText();
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            switch (decoder)
            {
                case "point": NativeCodec.DecodePoint(json); break;
                case "query": NativeCodec.DecodeQueryPage(json, 128); break;
                case "receipt": NativeCodec.DecodeReceipt(json); break;
                case "status": NativeCodec.DecodeStatus(json); break;
                case "error": NativeCodec.DecodeError(json); break;
                case "commit": NativeCodec.DecodeCommit(NativeCodecTests.Scope, json); break;
            }
            return Task.CompletedTask;
        });
        await Assert.That(failure!.Message).IsEqualTo("Native JSON exceeds its byte limit.");
        await Assert.That(typeof(NativeCodec).GetMethods().Any(method => method.Name.StartsWith("Decode", StringComparison.Ordinal) &&
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(JsonElement)))).IsFalse();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(128)]
    [Arguments(129)]
    [Arguments(int.MaxValue)]
    public async Task PublicQueryPageDecoderRequiresAValidRequestedLimit(int limit)
    {
        const string page = "{\"version\":1,\"rows\":[]}";
        if (limit is >= 1 and <= 128)
            await Assert.That(NativeCodec.DecodeQueryPage(page, limit).Rows.Length).IsEqualTo(0);
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            {
                NativeCodec.DecodeQueryPage(page, limit);
                return Task.CompletedTask;
            });
            await Assert.That(failure!.Message).IsEqualTo("Query limit must be between 1 and 128.");
        }
    }

    private sealed class CancellingProvider(string stage, CancellationToken unrelated) : INativeAdmissionProvider
    {
        public ValueTask<NativeAdmission> ResolveAsync(NativeScope scope, string apiKey, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken)
        {
            if (stage == "admission") throw new OperationCanceledException(unrelated);
            return ValueTask.FromResult(new NativeAdmission("https://gru.api.terrascale.tech", () =>
            {
                if (stage == "assert") throw new OperationCanceledException(unrelated);
            }));
        }
    }

    private sealed class OversizedContent : HttpContent
    {
        private readonly OversizedSerializer serializer;
        internal OversizedContent(OversizedSerializer serializer)
        {
            this.serializer = serializer;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => serializer.SerializeAsync(destination);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class DerivedOversizedContent : StreamContent
    {
        private readonly OversizedSerializer serializer;
        internal DerivedOversizedContent(OversizedSerializer serializer) : base(Stream.Null)
        {
            this.serializer = serializer;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => serializer.SerializeAsync(destination);
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context, CancellationToken cancellationToken) =>
            serializer.SerializeAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new InvalidOperationException("Custom content must not be prebuffered.");
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Custom content must not be prebuffered.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class OversizedSerializer
    {
        internal int BytesWritten { get; private set; }
        internal int AttemptedBytes { get; private set; }
        internal async Task SerializeAsync(Stream destination)
        {
            var chunk = new byte[8192];
            // No Content-Length or stream override: default ReadAsStreamAsync
            // would serialize all eight MiB into an unbounded internal buffer.
            for (var index = 0; index < 1024; index++)
            {
                AttemptedBytes += chunk.Length;
                await destination.WriteAsync(chunk);
                BytesWritten += chunk.Length;
            }
        }
    }

    private sealed class AdmissionFailureContent : HttpContent
    {
        internal AdmissionFailureContent() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) =>
            Task.FromException(new NativeAdmissionException("discovery_forbidden", 403));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
