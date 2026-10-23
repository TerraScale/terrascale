using System.Diagnostics;
using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// <see cref="Writes"/>: a background write that fails fails its scenario's gate at once, as a real failure with the
/// per-operation record, never as a convergence that did not come (the gap's own way to fail).
/// </summary>
[Category("local-only")]
public sealed class WritesTests
{
    private const string Signature = "a quarter of the writes";

    // Write 5 fails while the writes in flight with it never finish: the gate fails at once, long before its deadline, with
    // the writer's exception and which ids were accepted, failed and pending.
    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_failed_write_fails_the_gate_at_once_with_every_operations_outcome()
    {
        var (writes, ids) = Failing();
        var clock = Stopwatch.StartNew();
        var failed = await Assert.ThrowsAsync<WriterFailedException>(() => writes.UntilAcceptedAsync(ids.Length / 4, Signature));

        await Assert.That(clock.Elapsed).IsLessThan(Converge.Deadline / 4);
        await Assert.That(failed!.InnerException).IsTypeOf<HttpRequestException>();
        await Assert.That(failed.Message).Contains($"write {ids[5]} failed: HttpRequestException");
        await Assert.That(failed.Message).Contains($"5 of {ids.Length} writes accepted, 1 failed [{ids[5]}]");
        await Assert.That(failed.Message).Contains("in flight");
    }

    // Even when its message carries the gap's signature, a failed write is the scenario failing some other way: a listed
    // scenario fails (UnexpectedFailureException), and one that is not listed fails with the writer's own exception.
    [Test]
    [Scenario(Nodes.Harness)]
    public async Task A_failed_write_is_a_real_failure_never_the_gap()
    {
        var listed = await Assert.ThrowsAsync<UnexpectedFailureException>(() =>
            Expected.FailureAsync("a control gap", Signature, () => Failing().Writes.UntilAcceptedAsync(200, Signature)));
        await Assert.That(listed!.InnerException).IsTypeOf<WriterFailedException>();

        _ = await Assert.ThrowsAsync<WriterFailedException>(() => Gaps.RunAsync("not-a-gap", () => Failing().Writes.UntilAcceptedAsync(200, Signature)));
    }

    // Once the gate has passed, waiting for the rest fails with the same record.
    [Test]
    [Scenario(Nodes.Harness)]
    public async Task Waiting_for_every_write_fails_with_the_failed_operation()
    {
        var ids = Enumerable.Range(0, 20).Select(index => $"doc{index:D2}").ToArray();
        var writes = Writes.Start(ids, 4, (index, _) => index == 7
            ? Task.FromException(new HttpRequestException($"PUT {ids[index]} answered 503: {Signature}", null, HttpStatusCode.ServiceUnavailable))
            : Task.CompletedTask);

        var failed = await Assert.ThrowsAsync<WriterFailedException>(writes.CompletedAsync);
        await Assert.That(failed!.Message).Contains($"write {ids[7]} failed");
    }

    // 800 writes: 0-4 are accepted, 5 answers 500, and the rest hang as if their region stopped answering.
    private static (Writes Writes, string[] Ids) Failing()
    {
        var ids = Enumerable.Range(0, 800).Select(index => $"doc{index:D3}").ToArray();
        var hang = new TaskCompletionSource();
        var started = new TaskCompletionSource<Writes>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Writes.Start(ids, 8, (index, token) => index switch
        {
            < 5 => Task.CompletedTask,
            5 => FailAfterAcceptedAsync(),
            _ => hang.Task.WaitAsync(token),
        });
        started.SetResult(writes);
        return (writes, ids);

        async Task FailAfterAcceptedAsync()
        {
            // Callback indices do not order completion: establish the exact count before publishing the failure.
            var active = await started.Task;
            _ = await active.UntilAcceptedAsync(5, "the fixture's first five writes");
            throw new HttpRequestException($"PUT {ids[5]} answered 500: {Signature}", null, HttpStatusCode.InternalServerError);
        }
    }
}
