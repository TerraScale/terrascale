# TerraScale capacity benchmark (TS-761)

`benchmark/` defines the public-SDK end-to-end capacity benchmark for deployed
TerraBase across automatically discovered serving regions. **gru** and **sfo**
are the current bounded benchmark qualification targets, not a serving allowlist.
The shared contract is
[docs/protocol.md](docs/protocol.md).

**This slice is incomplete:** workloads, the bounded measurement engine, local
runner, HDR aggregation and Markdown reporting are implemented and tested.
The Go SDK client, TerraBase persistence and first live reports are blocked on
[the official SDK JWT data surface](clients/go/README.md). No compatibility
API-key path or synthetic capacity result replaces those requirements.

The merged [Rust client preflight](clients/rust/README.md) validates the CLI
contract and records its official SDK lifecycle, ULID and index prerequisites.
It remains blocked from generating load.
The [.NET client preflight](clients/dotnet/README.md) is SDK-linked; load generation awaits public SDK lifecycle/index APIs.

## Check locally

```sh
nice -n 19 make check
```

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
`TS_BENCH_CLIENT_SECRET`. Under the operating contract, OCI Vault creates and
delivers first-party credentials automatically, with vendor keys stored once.
This client/delivery integration is still pending; no manual secret copying is
an onboarding step and the preflights must not read credentials. The client
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

[Prerequisite evidence](bench/2026-10-08-prerequisites.md) records why no live
capacity number is claimed yet.
