# ts-auth SDK client benchmarks

Benchmarks for the official ts-auth management API clients across the four
TerraScale SDKs. Every harness drives the **official client** of its language
against a canned-response **in-process transport** (no socket, no web stack)
serving the exact ts-auth user route shapes, so each measured operation pays
the full client path — typed request, JSON encoding, HTTP client machinery,
JSON decode, response verification — against a constant service cost.

The .NET lane lives in the shared engine-comparison machinery (same class:
raw in-process handler) as engine `tsauth-sdk`:

```sh
nice -n 10 dotnet build terrabase/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release
taskset -c 0-3 nice -n 19 env DOTNET_PROCESSOR_COUNT=4 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet terrabase/TerraBase.Benchmarks/bin/Release/net11.0/TerraBase.Benchmarks.dll \
  --workload engine-comparison --engines tsauth-sdk \
  --samples 5 --operations 5000 --warmup 1000 --seed 1337 \
  --output terrabase/receipts/<date>-tsauth-sdk-<phase>
```

The Go harness resolves the sibling `ts-go` checkout through a module replace
(the sibling-subject convention `terrabase/` uses for its own subjects):

```sh
cd ts-auth/go && go build .
taskset -c 0-3 nice -n 19 env GOMAXPROCS=4 go run . \
  --samples 5 --operations 5000 --warmup 1000 \
  --output ../../../ts-benchmark/ts-auth/receipts/<date>-go-tsauth
```

## Lanes

Identical across the Go/Rust/TS harnesses (the .NET engine matrix is the same minus the mixed-c16 row):

| Lane | Operation | Concurrency |
| --- | --- | --- |
| read | `users.get` of `usr_{seq % 1000}` (~800-byte canned user) | c1, c16 |
| query | `users.list` page of 50 from cursor `usr_{seq % 1000}` (~40 KB) | c1 |
| write | alternating `users.create` and a two-field `users.update` patch (name + banned) | c1, c16 |
| mixed | alternating read and create | c1, c16 |

Envelope: 5 samples × 5000 operations after 1000 warmup operations, seed 1337 (recorded by the Rust/TS harnesses; the Go harness accepts and discards it),
dataset 1000 users with a 256-byte payload in `public_metadata`/`private_metadata`.
Every response is verified (wrong id/count fails the run). Metrics per sample:
throughput, p50/p95/p99 per-operation latency, allocated bytes per operation
(Go/.NET; recorded where the runtime exposes it). The Go/Rust/TS summaries list
raw per-sample rows; the .NET engine summary reports medians of per-sample
percentiles — compare like with like.

## Reading the numbers

- **Within one language**, before/after receipts under identical envelopes and
  seeds are the comparison contract (the standing repo rule: cross-day
  absolutes swing with the box; claim ratios, not absolutes).
- **Across languages** the numbers are directional only: each runtime brings
  its own JSON stack, allocator, and HTTP machinery.
- Receipts live under `ts-auth/receipts/<date>-<slug>/` (per-language harnesses)
  and `terrabase/receipts/<date>-tsauth-sdk-<phase>/` (.NET engine lane);
  dated reports at `ts-auth/*.md` narrate the comparisons.
