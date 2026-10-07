# Mixed-load engine (TS-721)

```sh
nice -n 19 env GOMAXPROCS=2 GOFLAGS=-p=1 make load-check
```

Run from the repository root with Go 1.25 or newer. This foundation package
uses only the standard library. It makes no production requests and requires
no credentials. The future production runner supplies an SDK callback to
`engine.Run`; no HTTP or authentication implementation belongs in this engine.

`Config` supplies the offered rate (up to 10,000 operations/s), concurrency,
duration, request timeout, drain timeout, and an `Execute(context, index, write)`
callback. Every fifth scheduled index is a write, preserving a deterministic
80/20 offered mix even when capacity drops an operation. The callback must
respect context cancellation and must not retry.

The monotonic schedule has no request queue. Timer wakes dispatch bounded batches
of at most 10 ms of due arrivals (100 operations at 10k ops/s), preserving each
arrival's scheduled time. Older missed slots are skipped; reaching the concurrency
cap drops an operation. Both remain visible in the report,
along with any unscheduled or unfinished work after cancellation. Requests
finish within a bounded drain. Percentiles cover successful calls only and use
nearest rank; empty sample sets have count zero and do not establish latency.

| Measurement | Boundary |
| --- | --- |
| Service read/write/all p50 and p99 | Callback start to validated completion |
| End-user read/write/all p50 and p99 | Scheduled arrival to validated completion, including dispatch delay |
| Successful operations/s | Completed successful operations / entire phase plus drain |
| Scheduler lag | Dispatch time minus scheduled arrival, for started operations |
| Errors, skipped, dropped, not scheduled, unfinished | Separate counts; offering 10k does not prove achieving 10k |

`MeetsThroughput` checks achieved successful throughput and rejects aborted or
unfinished runs. The consumer must also enforce read/write p99 targets, sample
counts, error budgets and lost arrivals before qualifying a production result.
A scheduler that cannot keep up must report its misses and cannot claim the
offered rate as achieved throughput. Batching delay stays in end-user latency.

Production integration remains a separate slice: verify gru-1 from beta/delta,
use the SDK's ts-auth JWT credentials, keep workload and results databases
separate, store run records in TerraBase, export comparison Markdown to
`bench/`, and produce the weekly report. The currently published ts-go data
constructor rejects JWTs and accepts only `tb_` keys; adding an auth shim or
loading through those older credentials would break the ownership rules.
