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
    public async Task LargeDecodeRemainsInsideCallerAndDeadlineIsolation(bool cancelCaller)
    {
        var context = Enumerable.Range(1, 10_000).Select(index =>
            new NativeObserved($"{index:x8}-0000-7000-8000-000000000005", new string('1', 32), 1)).ToImmutableArray();
        var point = new NativePoint(NativePointState.Deleted, new string('d', 64), context,
            [new NativeSibling(new NativeEvent(NativeCodecTests.Database, 0, context[0].Replica, context[0].Incarnation, 1),
                1, NativeCodecTests.SchemaDigest, new NativeWriteValue.Tombstone())]);
        var body = NativeCodec.EncodePoint(point);
        using var handler = new NativeClientTests.Handler((_, _) => Task.FromResult(NativeClientTests.Json(body)));
        var clock = new ManualDeadlineClock();
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task UndeclaredSerializingContentStopsAtTheBodyLimit(bool discovery)
    {
        var limit = discovery ? 64 * 1024 : NativeCodec.MaximumBodyBytes;
        using var content = new OversizedContent();
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
        await Assert.That(content.BytesWritten).IsEqualTo(limit);
        await Assert.That(content.AttemptedBytes).IsEqualTo(limit + 8192);
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

    private sealed class ManualDeadlineClock : TimeProvider
    {
        private ManualTimer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timer = new ManualTimer(callback, state);
        internal void Expire() => timer!.Fire();

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private readonly object gate = new();
            private bool disposed;
            internal void Fire() { lock (gate) { if (!disposed) callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (gate) return !disposed; }
            public void Dispose() { lock (gate) disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
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
        internal int BytesWritten { get; private set; }
        internal int AttemptedBytes { get; private set; }
        internal OversizedContent() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
        protected override async Task SerializeToStreamAsync(Stream destination, TransportContext? context)
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
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
