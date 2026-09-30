using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeClientTests
{
    internal static NativeClient Client(HttpMessageHandler handler, INativeAdmissionProvider? provider = null) => new(
        NativeCodecTests.Scope, provider ?? new NativeDirectAdmissionProvider("https://gru.terrascale.tech"),
        static _ => ValueTask.FromResult("tbk1.fixture-key"), TimeSpan.FromSeconds(2), handler);

    internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal static string Receipt(NativeTransactionIdentity identity) => NativeCodec.EncodeReceipt(new NativeCommitReceipt(
        NativeDurability.LocalDurable, identity, new NativeEvent(NativeCodecTests.Database, 0, NativeCodecTests.Replica, new string('1', 32), 1), 1));

    [Test]
    public async Task QueryContinuationKeepsPredicateLimitAndOpaqueCursorAndReauthenticates()
    {
        var bodies = new List<string>();
        var credentials = new List<string>();
        using var handler = new Handler(async (request, token) =>
        {
            await Assert.That(request.RequestUri!.AbsolutePath).EndsWith("/shards/0/collections/" + NativeCodecTests.Collection + "/documents/query");
            await Assert.That(request.Content!.Headers.ContentType!.ToString()).IsEqualTo("application/json");
            bodies.Add(await request.Content.ReadAsStringAsync(token));
            credentials.Add(request.Headers.Authorization!.Parameter!);
            return Json(bodies.Count == 1 ? "{\"version\":1,\"rows\":[],\"next_cursor\":\"opaque_cursor-1\"}" : "{\"version\":1,\"rows\":[]}");
        });
        var key = 0;
        using var client = new NativeClient(NativeCodecTests.Scope, new NativeDirectAdmissionProvider("https://gru.terrascale.tech"),
            _ => ValueTask.FromResult("tbk1.fixture-" + ++key), TimeSpan.FromSeconds(2), handler);
        var query = client.PrepareQuery(NativeCodecTests.Collection, new NativeQueryPredicate(NativeQueryOperator.Equal, [1], new NativeValue.Text("value")), 1);
        var first = await client.QueryAsync(query);
        var cursor = ((NativeResult<NativeQueryPage>.Success)first).Value.NextCursor;
        var second = await client.QueryAsync(query, cursor);
        await Assert.That(second is NativeResult<NativeQueryPage>.Success).IsTrue();
        using var page1 = NativeCodec.Parse(bodies[0]);
        using var page2 = NativeCodec.Parse(bodies[1]);
        await Assert.That(page1.RootElement.GetProperty("predicate").GetRawText()).IsEqualTo(page2.RootElement.GetProperty("predicate").GetRawText());
        await Assert.That(page2.RootElement.GetProperty("cursor").GetString()).IsEqualTo("opaque_cursor-1");
        await Assert.That(page2.RootElement.GetProperty("limit").GetInt32()).IsEqualTo(1);
        await Assert.That(credentials[0]).IsNotEqualTo(credentials[1]);
    }

    [Test]
    public async Task ExpiredAdmissionFailsBeforeAnyCredentialDispatch()
    {
        var calls = 0;
        using var handler = new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); });
        using var client = Client(handler, new StaleAdmission());
        var result = await client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document);
        await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("discovery_expired");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ExactCommitRetriesRetainOriginalBodyAndRejectChangedReceipts()
    {
        var bodies = new List<string>();
        NativeTransactionIdentity? identity = null;
        using var handler = new Handler(async (request, token) =>
        {
            await Assert.That(request.RequestUri!.AbsolutePath).EndsWith("/transactions");
            await Assert.That(request.Headers.Contains("Idempotency-Key")).IsFalse();
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (bodies.Count == 1) throw new HttpRequestException("producer fixture interrupted");
            return Json(Receipt(identity!));
        });
        using var client = Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        identity = prepared.Identity;
        var first = await client.CommitAsync(prepared);
        var failure = ((NativeResult<NativeCommitReceipt>.Failure)first).Error;
        await Assert.That(failure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        await Assert.That(failure.Transaction).IsEqualTo(identity);
        var second = await client.CommitAsync(prepared);
        await Assert.That(second is NativeResult<NativeCommitReceipt>.Success).IsTrue();
        await Assert.That(bodies[0]).IsEqualTo(bodies[1]);
        await Assert.That(bodies.Count).IsEqualTo(2);

        using var changedHandler = new Handler((_, _) => Task.FromResult(Json(Receipt(identity with { EffectDigest = new string('f', 64) }))));
        using var changedClient = Client(changedHandler);
        var mismatch = await changedClient.CommitAsync(changedClient.PrepareCommit(NativeCodecTests.CreateCommit()));
        await Assert.That(((NativeResult<NativeCommitReceipt>.Failure)mismatch).Error.Code).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task OnlyRetainedPreconditionFailureIsARejectedCommitOutcome()
    {
        foreach (var code in new[] { "precondition_failed", "invalid_key", "idempotency_conflict", "commit_uncertain" })
        {
            var status = code == "precondition_failed" ? HttpStatusCode.PreconditionFailed : code == "invalid_key"
                ? HttpStatusCode.Unauthorized : code == "commit_uncertain" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Conflict;
            using var handler = new Handler((_, _) => Task.FromResult(Json("{\"error\":{\"code\":\"" + code + "\"}}", status)));
            using var client = Client(handler);
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            var result = await client.CommitAsync(prepared);
            var failure = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
            await Assert.That(failure.Code).IsEqualTo(code);
            await Assert.That(failure.Transaction).IsEqualTo(prepared.Identity);
            await Assert.That(failure.MutationOutcome).IsEqualTo(code == "precondition_failed" ? NativeMutationOutcome.Rejected
                : code == "commit_uncertain" ? NativeMutationOutcome.Uncertain : NativeMutationOutcome.Unknown);
        }
    }

    [Test]
    public async Task UnknownNativeErrorIdentifiersCannotReachRetainedDiagnostics()
    {
        const string canary = "secret_canary";
        using var handler = new Handler((_, _) => Task.FromResult(Json(
            "{\"error\":{\"code\":\"" + canary + "\"}}", HttpStatusCode.BadRequest)));
        using var client = Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        var result = await client.CommitAsync(prepared);
        var failure = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
        await Assert.That(failure.Code).IsEqualTo("invalid_response");
        await Assert.That(failure.Status).IsEqualTo(400);
        await Assert.That(failure.Transaction).IsEqualTo(prepared.Identity);
        await Assert.That(failure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        await Assert.That(result.ToString()).DoesNotContain(canary);
    }

    [Test]
    public async Task ARejectedLateQueryRowDoesNotExposeEarlierRows()
    {
        const string absent = "{\"version\":1,\"state\":\"absent\",\"state_version\":null,\"context\":[],\"siblings\":[]}";
        var json = "{\"version\":1,\"rows\":[{\"document\":\"" + NativeCodecTests.Document + "\",\"point\":" + absent +
            "},{\"document\":\"018f0000-0000-7000-8000-000000000006\",\"point\":" + absent + ",\"unknown\":true}]}";
        using var handler = new Handler((_, _) => Task.FromResult(Json(json)));
        using var client = Client(handler);
        var query = client.PrepareQuery(NativeCodecTests.Collection, new NativeQueryPredicate(NativeQueryOperator.IsMissing, [1]), 2);
        var result = await client.QueryAsync(query);
        await Assert.That(result is NativeResult<NativeQueryPage>.Failure).IsTrue();
        await Assert.That(((NativeResult<NativeQueryPage>.Failure)result).Error.Code).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task ForeignScopeReceiptsCannotResolveThePreparedCommit()
    {
        NativeTransactionIdentity? identity = null;
        using var handler = new Handler((_, _) => Task.FromResult(Json(NativeCodec.EncodeReceipt(new NativeCommitReceipt(
            NativeDurability.LocalDurable, identity!, new NativeEvent(NativeCodecTests.Database, 1, NativeCodecTests.Replica, new string('1', 32), 1), 1)))));
        using var client = Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        identity = prepared.Identity;
        var result = await client.CommitAsync(prepared);
        var failure = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
        await Assert.That(failure.Code).IsEqualTo("invalid_response");
        await Assert.That(failure.Transaction).IsEqualTo(prepared.Identity);
        await Assert.That(failure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
    }

    [Test]
    public async Task CancellationAfterDispatchPreservesUncertainIdentityAndDoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var handler = new Handler((_, token) =>
        {
            calls++;
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var client = Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        var result = await client.CommitAsync(prepared, cancellation.Token);
        var failure = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
        await Assert.That(failure.Code).IsEqualTo("cancelled");
        await Assert.That(failure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        await Assert.That(failure.Transaction).IsEqualTo(prepared.Identity);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task TestApiOriginsAndUnmarkedLoopbackAreRejectedLocally()
    {
        foreach (var origin in new[] { "https://api.terrascale.tech", "https://gru.api.terrascale.tech", "https://API.TERRASCALE.TECH.", "http://127.0.0.1:1" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                NativeOrigin.Validate(origin);
                return Task.CompletedTask;
            });
        }
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class StaleAdmission : INativeAdmissionProvider
    {
        public ValueTask<NativeAdmission> ResolveAsync(NativeScope scope, string apiKey, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NativeAdmission("https://gru.terrascale.tech", static () => throw new NativeAdmissionException("discovery_expired")));
    }
}
