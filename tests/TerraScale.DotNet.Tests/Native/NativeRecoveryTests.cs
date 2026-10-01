using System.Net;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeRecoveryTests
{
    [Test]
    public async Task RecoveryPollsOnlyTheOriginalStatusAndReturnsItsReceipt()
    {
        var calls = 0;
        var bodies = new List<string>();
        NativeTransactionIdentity? identity = null;
        using var handler = new NativeClientTests.Handler(async (request, token) =>
        {
            calls++;
            await Assert.That(request.RequestUri!.AbsolutePath).EndsWith("/transactions/status");
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (calls == 2)
            {
                var response = NativeClientTests.Json("{\"error\":{\"code\":\"rate_limited\"}}", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            }
            var state = calls == 1 ? NativeTransactionState.Pending : calls == 3 ? NativeTransactionState.Unknown : NativeTransactionState.Committed;
            NativeCommitReceipt? receipt = state == NativeTransactionState.Committed ? new NativeCommitReceipt(
                NativeDurability.ObjectDurable, identity!, new NativeEvent(NativeCodecTests.Database, 0, NativeCodecTests.Replica, new string('1', 32), 1), 1) : null;
            return NativeClientTests.Json(NativeCodec.EncodeTransactionStatus(new NativeTransactionStatus(identity!, state, receipt)));
        });
        using var client = NativeClientTests.Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        identity = prepared.Identity;
        var result = await NativeRecovery.ResolveAsync(client, prepared, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
        await Assert.That(result.IsResolved).IsTrue();
        await Assert.That(result.State).IsEqualTo(NativeTransactionState.Committed);
        await Assert.That(result.Receipt!.Identity).IsEqualTo(prepared.Identity);
        await Assert.That(result.Receipt.Durability).IsEqualTo(NativeDurability.ObjectDurable);
        await Assert.That(calls).IsEqualTo(4);
        await Assert.That(bodies.All(body => body == bodies[0])).IsTrue();
    }

    [Test]
    public async Task ExpiryAndAuthenticationFailureNeverProveTheOriginalCommitAborted()
    {
        foreach (var expired in new[] { true, false })
        {
            var calls = 0;
            NativeTransactionIdentity? identity = null;
            using var handler = new NativeClientTests.Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(expired
                    ? NativeClientTests.Json(NativeCodec.EncodeTransactionStatus(new NativeTransactionStatus(identity!, NativeTransactionState.Expired)))
                    : NativeClientTests.Json("{\"error\":{\"code\":\"invalid_key\"}}", HttpStatusCode.Unauthorized));
            });
            using var client = NativeClientTests.Client(handler);
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            identity = prepared.Identity;
            var result = await NativeRecovery.ResolveAsync(client, prepared, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
            await Assert.That(result.IsResolved).IsFalse();
            await Assert.That(result.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
            await Assert.That(result.Identity).IsEqualTo(prepared.Identity);
            await Assert.That(calls).IsEqualTo(1);
        }
    }

    [Test]
    public async Task RecoveryDistinguishesItsOwnDeadlineFromCallerCancellation()
    {
        foreach (var cancelCaller in new[] { false, true })
        {
            using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var calls = 0;
            using var handler = new NativeClientTests.Handler(async (_, token) =>
            {
                calls++;
                if (cancelCaller) caller.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return NativeClientTests.Json("{}");
            });
            using var client = NativeClientTests.Client(handler);
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            var result = await NativeRecovery.ResolveAsync(client, prepared, TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100), caller.Token);
            await Assert.That(result.LastFailure!.Code).IsEqualTo(cancelCaller ? "cancelled" : "deadline_exceeded");
            await Assert.That(result.LastFailure.Transaction).IsEqualTo(prepared.Identity);
            await Assert.That(result.LastFailure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
            await Assert.That(result.Identity).IsEqualTo(prepared.Identity);
            await Assert.That(result.IsResolved).IsFalse();
            await Assert.That(calls).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RecoveryWaitsForCancellationOrDeadlineWhenTheNextDelayCannotFit(bool retryAfter, bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var responseSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        NativeTransactionIdentity? identity = null;
        using var handler = new NativeClientTests.Handler((_, _) =>
        {
            calls++;
            var response = retryAfter
                ? NativeClientTests.Json("{\"error\":{\"code\":\"rate_limited\"}}", HttpStatusCode.TooManyRequests)
                : NativeClientTests.Json(NativeCodec.EncodeTransactionStatus(new NativeTransactionStatus(identity!, NativeTransactionState.Pending)));
            if (retryAfter) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            responseSent.SetResult();
            return Task.FromResult(response);
        });
        using var client = NativeClientTests.Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
        identity = prepared.Identity;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var recovery = NativeRecovery.ResolveAsync(client, prepared, TimeSpan.FromMilliseconds(500),
            retryAfter ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), caller.Token);
        await responseSent.Task;
        var completedBeforeCancellation = false;
        if (cancelCaller)
        {
            await Task.Delay(30);
            completedBeforeCancellation = recovery.IsCompleted;
            caller.Cancel();
        }
        var result = await recovery;
        await Assert.That(completedBeforeCancellation).IsFalse();
        await Assert.That(result.LastFailure!.Code).IsEqualTo(cancelCaller ? "cancelled" : "deadline_exceeded");
        await Assert.That(result.LastFailure.Transaction).IsEqualTo(prepared.Identity);
        await Assert.That(result.LastFailure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        await Assert.That(calls).IsEqualTo(1);
        if (!cancelCaller)
            await Assert.That(System.Diagnostics.Stopwatch.GetElapsedTime(started)).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(400));
    }

    [Test]
    public async Task RetainedBodyRestoresAcrossClientsWithoutSerializingCredentials()
    {
        var directory = Directory.CreateTempSubdirectory("terrascale-native-retained-");
        var path = Path.Combine(directory.FullName, "operation.json");
        try
        {
            using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No request is expected."));
            using var client = NativeClientTests.Client(handler);
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            await NativeOperationStore.SaveAsync(prepared, path);
            var retained = await NativeOperationStore.LoadAsync(path);
            var rawFile = await File.ReadAllTextAsync(path);
            await Assert.That(rawFile).DoesNotContain("tbk1.fixture-key");
            await Assert.That(rawFile).DoesNotContain("authorization");
            if (!OperatingSystem.IsWindows()) await Assert.That(File.GetUnixFileMode(path))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);

            using var secondHandler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No request is expected."));
            using var second = NativeClientTests.Client(secondHandler);
            var restored = second.RestoreCommit(retained);
            await Assert.That(restored.RequestBody).IsEqualTo(prepared.RequestBody);
            await Assert.That(restored.Identity).IsEqualTo(prepared.Identity);
            await Assert.That(restored.ToString()).DoesNotContain("first note");
            await Assert.ThrowsAsync<IOException>(() => NativeOperationStore.SaveAsync(prepared, path));

            var corrupt = retained with { RequestBody = retained.RequestBody.Replace("first note", "other note", StringComparison.Ordinal) };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
            {
                second.RestoreCommit(corrupt);
                return Task.CompletedTask;
            });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
            {
                second.RestoreCommit(retained with { Scope = retained.Scope with { Shard = 1 } });
                return Task.CompletedTask;
            });
        }
        finally { directory.Delete(recursive: true); }
    }
}
