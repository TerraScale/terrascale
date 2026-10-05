# ts-benchmark

Reproducible HTTPS acceptance tooling for the **C# TerraBase** server. Tests
start a real release binary locally with verifying TLS and fresh storage; no
fake server or production data is used.

| Path | Purpose |
| --- | --- |
| `https-document/` | Project-scoped C# Document API driver and local release harness |
| `https-document/release.json` | Exact release version, source revision, download URL and SHA-256 |
| `bench/csharp-baseline/` | Committed baseline JSON, raw samples, environment and Markdown table |
| `local-engine/` | Retained pinned SQLite/Turso comparison subject |
| `terra-rust/`, `firecracker-harness/` | Historical Rust qualification and optional microVM tooling |

## C# acceptance

The harness downloads the pinned Linux amd64 release, verifies its committed
SHA-256 before execution, creates a temporary TLS certificate and dashboard
credential, starts the server, and provisions an organization, project,
database, collection and a database-scoped `tb_` API key. The Management API
uses the dashboard credential and acting-user/organization headers. Key
issuance uses only the local loopback administrative listener. Setup data lives
in the same real TerraBase server under test.

```sh
nice -n 19 make fmt check lint build
nice -n 19 make -C https-document test
nice -n 19 make csharp-smoke
nice -n 19 python3 https-document/acceptance.py --output bench/csharp-baseline/NEW_RUN_ID
```

Prerequisites: the pinned Rust toolchain, Python 3.8+, OpenSSL, Linux amd64, and
an existing `fj` login or `ORG_READ_TOKEN` with release read access. Cargo and
rustfmt must come from rustup; with mise use `nice -n 19 mise exec -- make …`.
Without mise, use `nice -n 19 env -u RUSTUP_TOOLCHAIN PATH="$HOME/.cargo/bin:$PATH"
make …`. The harness honors Cargo target overrides when locating the driver.
Explicit output directories must be fresh; evidence is never overwritten. Smoke
commands create a unique run subdirectory under `bench/csharp-smoke/`, so the
same command can be repeated after success or failure.

The fixed concurrency levels are **1, 4, 16**. Each runs point reads, point
writes, queries with a filter and descending field sort, two-put transactions,
and an exact 90% read / 10% write mix. Baseline runs use 100 operations per
worker per workload; smoke uses 10. Every response is validated. All write
cases use conditional creates with document IDs unique across the entire run,
namespaced by workload, concurrency, worker, operation and transaction slot.
This keeps the workload consistently insert-only across cases. After each case, every
write is read back, including both transaction documents and every write in the
90% read / 10% write mix. Readbacks are excluded from latency and throughput
timing. A failed transaction is checked for rollback before measurement.
To bound memory, each concurrency level may produce at most 100,000 samples
per workload. JSON evidence records p50/p95/p99, successful operations/second, errors, binary
hashes, timing boundaries and `write_mode: "insert_only"`. Raw samples and a
Markdown table accompany it.

The driver resolves Atlas once and pins its selected read/write endpoint for
the session. `endpoint_override` bypasses discovery; the local harness always
sets it to its temporary HTTPS listener. No regional production origin is
hardcoded in client code. The pinned baseline is a published CI prerelease,
not a stable `v*` release; changing that pin requires a new baseline.

## CI and historical comparisons

[Fast CI](.forgejo/workflows/fast.yml) runs the C# HTTPS acceptance smoke.
The lane checks formatting, strict Clippy, unit checks and the
real released server, and retains JSON/Markdown artifacts even on failure.
It fails on correctness, setup, transport or API errors, **never performance
numbers**. CI runs at full CPU priority; callers apply `nice -n 19` locally.
No KVM, OCI provisioning, production API call, DNS or secret change is needed.

The retained SQLite/Turso smoke runs identical CRUD traces, reopens both
engines, checks integrity and compares final-state/trace hashes. Its historical
Rust sources live at `../terrabase`, pinned by `terrabase.rev`, solely to supply
Turso bindings and patches. They are not the current C# product. The historical
checkout helper is `scripts/ci/checkout-terrabase.sh`; locally run
`nice -n 19 bash tests/local-engine.sh` when that historical checkout is available.
The historical commit is no longer available from the current C# origin, so
these optional comparison subjects are excluded from active product CI.

Rust engine qualification and its synthetic throughput-floor tests are retired
from active CI. Existing Rust matrices and receipts remain unchanged as
history; they do not qualify the C# server. SQLite/Turso timings and HTTPS
process timings have different boundaries and are not directly comparable.

## Evidence

All benchmarks have easy-to-compare Markdown tables in `bench/`, with links to
JSON evidence. Preserve failed runs and use unique run directories. The
[current insert-only baseline](bench/csharp-baseline/ts500-20261004-inserts/summary.md)
covers 10,500 logical operations and 6,510 write readbacks. The
[all-writes baseline](bench/csharp-baseline/ts500-20261004-all-writes/summary.md)
remains historical: every write was checked, but its insert/update mix depended
on the case. The [original baseline](bench/csharp-baseline/ts500-20261004/summary.md)
remains historical with final overwritten-value readbacks only. C# receipts
are process acceptance observations; the server and load generator share the
host, and local low-priority scheduling affects timings. They establish API
correctness and a reproducible workload baseline, not a production-region SLA
or a Rust matrix throughput verdict. See [bench/README.md](bench/README.md).
