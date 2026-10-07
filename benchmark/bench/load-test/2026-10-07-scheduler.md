# Client scheduler calibration — beta, 2026-10-07

Synthetic no-op callbacks only: **no TerraBase requests, production throughput
claim, or durable result record**. This checks whether the client's scheduler
can offer the TS-721 workload before adding the SDK adapter. Both designs use
64 concurrent calls, a one-second offered phase, one-second request and drain
timeouts, deterministic 80/20 arrival selection, and no retries.

Client: beta, Linux amd64, AMD Ryzen 5 PRO 6650H, Go 1.27.1. Commands ran at
nice 19 with `GOMAXPROCS=2` and `GOFLAGS=-p=1` on the shared host.

| Scheduler | Offered ops/s | Successful ops/s | Arrival p50 ms | Arrival p99 ms | Successful reads / writes | Skipped | Dropped | Errors |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Initial, newest arrival only | 100 | 99.95 | 0.7296 | 4.380 | 80 / 20 | 0 | 0 | 0 |
| Initial, newest arrival only | 1,000 | 628.5 | 0.5193 | 1.029 | 502 / 128 | 370 | 0 | 0 |
| Initial, newest arrival only | 5,000 | 771.2 | 0.1041 | 0.2514 | 569 / 203 | 4,228 | 0 | 0 |
| Initial, newest arrival only | 10,000 | 948.0 | 0.05864 | 0.1600 | 730 / 220 | 9,050 | 0 | 0 |
| Bounded due-arrival batch | 100 | 99.81 | 0.6563 | 2.797 | 80 / 20 | 0 | 0 | 0 |
| Bounded due-arrival batch | 1,000 | 998.9 | 0.7761 | 5.675 | 800 / 199 | 1 | 0 | 0 |
| Bounded due-arrival batch | 5,000 | 4,996 | 0.5633 | 2.814 | 4,000 / 1,000 | 0 | 0 | 0 |
| Bounded due-arrival batch | 10,000 | 9,990 | 0.5740 | 3.558 | 8,000 / 1,999 | 1 | 0 | 0 |

Successful throughput divides validated completions by the entire offered phase
plus drain. Arrival latency starts at the original scheduled arrival; samples
include successful operations only. The initial design's low p99 at high rates
reflects skipping most arrivals, which is why percentiles alone cannot qualify
a result. The revised design dispatches at most 10 ms of due arrivals per wake
(100 calls at 10k), keeps their scheduled timestamps, and skips older backlog.
Actual callback concurrency remains capped independently of that batch size.

These are single observations on a shared host, captured from the uncommitted
implementation with other low-priority compiler work active. They do not
establish a stable capacity floor or isolate batching's causal effect from host
load. The initial working-tree source hash was not recorded. Re-run the final
implementation before using this comparison to qualify a production client.

Reproduce the final scheduler from `load-test/`:

```sh
nice -n 19 env GOMAXPROCS=2 GOFLAGS=-p=1 \
  go test ./engine -run '^$' -bench '^BenchmarkSchedule$' -benchtime=1x -count=1
```

Production result storage and weekly reporting belong to the next slice; this
table is a development benchmark artifact, not an application result store.
