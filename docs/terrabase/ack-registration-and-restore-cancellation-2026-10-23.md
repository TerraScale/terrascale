# ACK registration and restore cancellation races

## ACK registration (#513)

`AckedAsync` reads a missing or stale peer cursor in a read transaction, then registers zero in a separate write transaction. A real `AckAsync` can commit between those transactions. The old unconditional zero reset then overwrote that ACK. The benchmark bootstrap barrier correctly kept waiting for the lost ACK until its two-minute deadline.

The zero update now rechecks staleness under the writer. It resets another incarnation or a cursor past the current CDC head, and preserves a valid same-incarnation ACK. No persisted format or API changes are needed. A reader racing that first registration can still return zero once; its next read observes the committed ACK.

The Fast integration regression queues a real ACK from the existing reader-reset checkpoint, before registration acquires the writer. It requires a positive head and an initial zero cursor, proving that the registration path ran. Against the old code it failed with **expected 7, actual 0**. It passes with the conditional update.

Original failure: [Slow run 40001424152, attempt 2](https://github.com/TerraScale/terrascale/actions/runs/40001424152/attempts/2), job `120611367102`. Bootstrap was still waiting for `own-prefix` at cancellation, 2m 04s into the test. The progress-file write in the stack is where cancellation was observed, not the cause.

## Completed header callback (#515)

The failing line waited for a callback registered by the snapshot header stream, after that read had already completed. It did not wait for a header: that earlier barrier had passed.

The same request token reaches both the header and the later data-key slot wait. Cancellation invokes newer callbacks first. Cancelling the slot waiter schedules its continuation asynchronously; that continuation can unwind the restore and dispose the snapshot response before cancellation reaches the older header callback. Stream disposal unregisters that callback. The restore has cancelled and cleaned up correctly, but the test's callback signal never completes.

The integration test now observes the overlapping server request finishing while its request token is cancelled, using the existing startup-filter pattern from `BackupEmptySegmentTests`. Instrumentation confirmed that the pipeline can return normally with `RequestAborted` cancelled; requiring a particular server exception would be incorrect. It still requires snapshot disposal, client cancellation, zero key GETs for the cancelled waiter, the first restore remaining blocked, all eight admissions being reusable, exact restored documents, exact GET/unwrap counts, and disposal of every stalled body. Every existing 30-second deadline is unchanged.

The pinned .NET runtime's [SemaphoreSlim implementation](https://github.com/dotnet/dotnet/blob/3551975be08744f0418857c5bed8ab1545c5dd47/src/runtime/src/libraries/System.Private.CoreLib/src/System/Threading/SemaphoreSlim.cs#L780) uses `ForceYielding` after cancellation. Its [CancellationTokenSource](https://github.com/dotnet/dotnet/blob/3551975be08744f0418857c5bed8ab1545c5dd47/src/runtime/src/libraries/System.Private.CoreLib/src/System/Threading/CancellationTokenSource.cs#L750) removes registrations from the head of the callback list.

A scratch console targeting the pinned `net11.0` runtime reproduced this valid ordering **100/100 times in 223 ms**. It held the cancelling thread with a bounded spin while the cancelled semaphore waiter disposed the older registration. The older callback never ran, while cleanup completed in every iteration. The essential reproduction is:

```csharp
using var cancellation = new CancellationTokenSource();
using var slots = new SemaphoreSlim(0, 1);
var called = 0;
var disposed = 0;
var header = cancellation.Token.Register(() => Interlocked.Increment(ref called));
using var preempted = cancellation.Token.Register(() =>
{
    if (!SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, TimeSpan.FromSeconds(5)))
        throw new InvalidOperationException("cleanup did not complete");
});
var cleanup = CleanAsync();
await cancellation.CancelAsync();
await cleanup;
if (called != 0 || disposed != 1)
    throw new InvalidOperationException("unexpected callback order");

async Task CleanAsync()
{
    try { await slots.WaitAsync(cancellation.Token); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        header.Dispose();
        Volatile.Write(ref disposed, 1);
    }
}
```

Original failure: [Fast run 40012196609](https://github.com/TerraScale/terrascale/actions/runs/40012196609). No production backup code changes are needed; the snapshot encryption work in PR #464 is unaffected.

## Local repeat evidence

Release builds used SDK `11.0.100-rc.1.26425.128`, a private ts-dotnet checkout at CI pin `bb7a398f246251683595959c7c22074f5fb5f232`, and `nice -n 19 ts-heavy`. Each row is a fresh Fast process followed by a fresh Slow process, with default test parallelism. All 25 test executions passed without skips. Times below are whole-test seconds, including setup and cleanup, rather than the individual 30-second waits.

| Repeat | Restore wait | Restore cancel (#515) | Bootstrap 2 regions (#513) | Bootstrap 3 regions |
|---|---:|---:|---:|---:|
| 1 | 7.264 | 12.315 | 7.348 | 7.346 |
| 2 | 24.813 | 34.538 | 5.858 | 6.223 |
| 3 | 18.474 | 20.989 | 7.108 | 7.200 |
| 4 | 20.928 | 23.807 | 6.212 | 6.212 |
| 5 | 18.381 | 25.923 | 7.648 | 7.788 |

The Fast filter also ran the new ACK regression in each process. Reproduce after building with `make terrabase-test-fast` and `make terrabase-test-slow`:

```sh
nice -n 19 ts-heavy env TsDotnetRoot=/path/to/ci-pinned-ts-dotnet \
  dotnet run --project tests/TerraBase.Fast --configuration Release --no-build -- \
  --treenode-filter '/*/*/AckRegistrationTests|DataKeyOverlapTests/*' --minimum-expected-tests 3
nice -n 19 ts-heavy env TsDotnetRoot=/path/to/ci-pinned-ts-dotnet \
  dotnet run --project tests/TerraBase.Slow --configuration Release --no-build -- \
  --treenode-filter '/*/*/*/Bootstrap_requires_physical_databases_and_all_peer_acks_through_a_frozen_cutoff' --minimum-expected-tests 2
```
