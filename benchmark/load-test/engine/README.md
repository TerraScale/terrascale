# Mixed-load engine

Stdlib-only package for an SDK CLI. The module lives in `load-test/`; the
production SDK adapter is a separate slice.

```go
report, err := engine.Run(ctx, engine.Config{
    Rate:           10_000,
    Concurrency:    128,
    Duration:       30 * time.Second,
    RequestTimeout: 2 * time.Second,
    DrainTimeout:   3 * time.Second,
    Execute:        func(ctx context.Context, index int, write bool) error {
        // Make one SDK operation; validate its response. Respect ctx; no retries.
        return executeSDKOperation(ctx, index, write)
    },
})
```

All numeric configuration fields must be positive. Rate is an integer from
1 to 10,000 ops/s. Planned operations are `ceil(Rate * Duration.Seconds())`,
capped at 3,000,000 per phase. Index zero is scheduled at phase start;
`index % 5 == 4` is a write. Complete five-index groups are exactly 80% reads
and 20% writes. A partial final group follows the same deterministic rule.
Skips/drops preserve logical indices, so the completed mix can differ under load.

The schedule uses Go's monotonic time and absolute offsets, rather than pacing
from completion times. A late wake dispatches at most 10 ms of due arrivals,
rounded up to a whole operation: at most 100 operations at the maximum rate.
Older slots are skipped, and each operation retains its original scheduled time
for end-user latency. A full concurrency limit drops an attempt. No requests are
queued or retried, and catch-up work is bounded. At most `min(Concurrency, Planned)`
callbacks and that many buffered results can be outstanding. Successful latency
samples are retained only for this capped phase; exact nearest-rank p50/p99
are calculated once at the end.

`ServiceLatency` measures callback execution. `EndUserLatency` measures from
the operation's original scheduled time through completion, including scheduler
and goroutine delay. Both split successful samples into all/read/write.
`SchedulerLag` records dispatch delay for every started operation, including
errors. Failed and unfinished requests do not enter successful percentiles.

The report obeys these accounting identities:

```text
Planned = Started + Skipped + Dropped + NotScheduled
Started = SuccessfulReads + SuccessfulWrites + Errors + Unfinished
SuccessfulOpsPerSecond = (SuccessfulReads + SuccessfulWrites) / Elapsed.Seconds()
```

`Elapsed` includes the full offered phase and any drain, ending before percentile
calculation. Even fast callbacks must wait out the phase. A normal drain ends
when all callbacks finish, with an absolute cap at `Duration + DrainTimeout`.
Every request has its own timeout and also inherits the parent and drain
deadlines. Parent cancellation stops scheduling and cancels requests immediately;
results can drain for at most `DrainTimeout`. `NotScheduled` counts remaining
planned slots after cancellation.

Callback failures, including individual request timeouts, increment `Errors`
without aborting the phase. Returning nil after context expiry is also an error.
Cancellation and drain expiry return a non-nil error and set `Aborted`.
`Unfinished` counts outstanding callbacks at drain expiry. `MeetsThroughput(min)`
compares actual successful ops/s to a positive finite minimum and refuses
aborted/unfinished phases. The CLI must separately gate errors and latency.

Callbacks must be safe for concurrent use and honor their context. Go cannot
forcibly stop an ignoring callback: Run still returns at the bounded drain,
reports it as unfinished, and that goroutine exits only when the callback does.
Its late result cannot mutate the returned report or block on the bounded buffer.

Local checks from the repository root (substitute the installed Go path if needed):

```sh
nice -n 19 env GOMAXPROCS=2 GOFLAGS=-p=1 make load-check
```

No benchmarks or external requests are run by these tests. A calling benchmark
should save its evidence as Markdown tables in `bench/`.
