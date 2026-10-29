# TerraScale capacity benchmark (TS-761)

`benchmark/` defines the public-SDK end-to-end capacity benchmark for deployed
TerraBase across automatically discovered serving regions. **gru** and **sfo**
are the current bounded benchmark qualification targets, not a serving allowlist.
The shared contract is
[docs/protocol.md](docs/protocol.md).

**This slice is incomplete:** workloads, the bounded measurement engine, local
runner, HDR aggregation and Markdown reporting are implemented and tested.
The TS-761 capacity Go client is not implemented yet, and TerraBase persistence
and first live reports remain blocked. The TS-832 API benchmark
([docs/api-bench.md](docs/api-bench.md)) has a Go client in
[clients/go](clients/go/README.md). No compatibility API-key path or synthetic
capacity result replaces those requirements.

The merged [Rust client preflight](clients/rust/README.md) validates the CLI
contract and records its official SDK lifecycle, ULID and index prerequisites.
It remains blocked from generating load.
The [.NET client preflight](clients/dotnet/README.md) is SDK-linked; load generation awaits public SDK lifecycle/index APIs.

## Check locally

```sh
nice -n 19 make check
```

`make test-fast` is the pre-push suite (`ts-ship/benchmark` runs
`make -C benchmark test-fast`). It runs the main-feature Go tests in parallel
with the race detector, uncached, with a four-minute per-package deadline.
`make test-slow` runs the exhaustive 108-cell parity matrix across three regions,
three repeats, three transport lanes, two payload sizes and two concurrency
levels, plus the PITR multi-batch, crash, fault, restore-boundary and exhaustive
data-oracle scenarios. Fast keeps a small write/update/delete/recreate, exact-cut
and verification lifecycle alongside client isolation and monitoring. `make test` and `make check` include both suites. Fast retains individual
transport-lane, measurement, document-validation and owned-database cleanup
coverage. All parity tests use local HTTPS fixtures.

Go 1.25+ is required. Local build, test and benchmark commands run at low CPU
priority; CI runs at full priority. Tests exercise scheduling and protocol
accounting without contacting production.

## Workloads

| File | Operations | Measurement |
| --- | --- | --- |
| [point-reads](workloads/point-reads.json) | Point get | 60 seconds, concurrency 8 |
| [write-heavy](workloads/write-heavy.json) | Create/update/delete 50/40/10 | 60 seconds, concurrency 8 |
| [mixed-70-30](workloads/mixed-70-30.json) | Get/update 70/30 | 60 seconds, concurrency 8 |
| [query](workloads/query.json) | Simple/indexed query 50/50 | 60 seconds, concurrency 8 |
| [txn](workloads/txn.json) | Two-document atomic transaction | 60 seconds, concurrency 8 |
| [ramp](workloads/ramp.json) | Get/update 70/30 | Seven 15-second steps, concurrency 1–64 |

All start with 128 documents of 1 KiB, five seconds of warmup and a 200 ms p99
target. Ramp stops after any measured second with error rate >1% or p99 >400 ms.
Setup, requests, drain and cleanup must fit within 15 minutes. Never run on
`mariogk-desktop`.

## Local runner

No conforming load client is available yet. The operating contract requires
automatic eligible-region discovery and local region inference, with no
serving-region activation or persistence toggle. The current runner still
requires a closed region selector and exposes a storage switch; those paths
are inventoried for implementation removal, not approved production setup.
Historical preflight receipts retain their original arguments as evidence.

Repeat `--client` for different language binaries (up to three). Local clients
run sequentially with a shared bound; combinations whose measurement plus
warmup exceeds 870 seconds are rejected before launch. Or supply `--input`
repeatedly to merge existing protocol JSONL. Every summary must include a
`workload_config` matching the requested workload. Percentiles come from merged
HDR histograms; throughput
uses successful completions divided by the full union of run windows, so
sequential clients do not masquerade as simultaneous load.

Evidence is append-only under `bench/<region>/<ULID>/`: `report.json`,
`summary.md`, and client JSONL. Markdown reports also land under
`reports/<region>/<UTC-timestamp>-<workload>-<report-ULID>.md`. Repeated imports
retain distinct reports. Failed or malformed client output
leaves a failure receipt and Markdown table with numbers marked unavailable.

The current evidence-only storage bypass cannot qualify production capacity.
The normal persistence path returns an error after retaining local evidence:
TerraBase persistence awaits the same SDK prerequisite as the Go client.
Cluster metrics are recorded as **unavailable** until slice 4's actual API
merges; zeroes are never substituted.

## Credentials and admission

The final SDK client consumes `TS_BENCH_CLIENT_ID` and
`TS_BENCH_CLIENT_SECRET`. Their delivery integration is still pending; the
preflights must not read credentials. The client
must be a dedicated project-scoped OAuth credential with database read/write
and document read/write grants. A service slug alone has no tenant authority.
Plain ULIDs identify runs, databases and stored entities. Credentials, JWTs and
SDK request/response bodies must never enter reports or logs.

The archived Rust harness, Firecracker fixtures, SQLite/Turso comparison,
old acceptance matrices and non-SDK HTTPS acceptance tooling are removed.
TS-648's in-process actor transport harness, regression fixtures and transport
receipts are also removed after rebasing on main `18c98fd1` (PR #11). It calls
the internal `IActorRouter` with synthetic UUID tenants and CPU callbacks; its
closed-loop array percentiles and combined process CPU/RSS do not measure
deployed TerraBase through public SDKs. Its Zipf machinery is not folded into
the six contracted workloads. The original diagnostic remains in Git history.

TS-721's absolute schedule, bounded admission and intended-start latency
principles are retained in `internal/benchmark`; the old fixed 80/20 client is
superseded. TS-606's direct regional HTTPS measurement and honest unavailable
metrics are retained as protocol requirements.

No live capacity number is claimed; retained reports state their measured environment.

## TS-832 API benchmark

Measures the production API through ts-go, ts-dotnet (JIT and Native AOT) and
raw-HTTP baselines. The contract is [docs/api-bench.md](docs/api-bench.md); the
Go client is [clients/go](clients/go/README.md).

Run it against production; the key comes from the file named by `TS_BENCH_KEY_FILE` (see the credentials section of the contract):

```sh
nice -n 19 make api-bench CLUSTER_STATE=stable   # or CLUSTER_STATE=changing
```

Reports land in `bench/results/` when a run produces evidence.

## Cloud benchmark boundary

Production is moving to OCI under `ts-orch`, starting with host `ts-gru-1`. Region codes map to OCI regions: `gru` → `sa-saopaulo-1`, `sjc` → `us-sanjose-1`, `fra` → `eu-frankfurt-1`. CI uses GitHub-hosted runners and GHCR.

Production secrets belong in OCI Vault; authorized hosts read narrowly granted secrets with instance principals. Do not copy production credentials into benchmark reports or target files. See the orchestrator's [secrets contract](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/secrets.md).

The local replication, PITR and shape harnesses remain local evidence. A future cloud run must supply verified per-region endpoints and measured host limits; no retired cloud measurement is evidence for OCI capacity. The shape load command currently starts only the local two-node host.
