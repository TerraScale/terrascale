# Astra review packet: TerraBase benchmark harness fix

Batch: `benchmark-20260907-harness-fix`
Prepared: 2026-09-08 UTC
Owner: harness worker (TB-BENCH-2); packet preparer: review lane

## Scope and outcome

The reviewed dirty slice is the single authored change to
`bench/scripts/run-terrabase-final.sh`. It wires the fresh database's API and
bearer credential into the local auto-index driver and supplies ingest's health,
version, and authorization variables. It also requires the verified member key
and version before starting. The complete diff is [harness-fix.diff](harness-fix.diff).

The benchmark lane evidence is included as context for the consuming run, not
as proof that this harness fix has passed the full final orchestrator. The latest
independent point run remains failed and is recorded below; no benchmark numbers
are asserted from it.

## Source identity and changed paths

| Repository | Base | Reviewed state | Changed paths | Identity |
| --- | --- | --- | --- | --- |
| terrabase | `fae32d994b5096f87ec9fa91166e471516afec1b` (`develop`) | dirty working tree, exact script hash below | `M bench/scripts/run-terrabase-final.sh` | `bench/scripts/run-terrabase-final.sh` SHA-256 `4eb0272d912a988f7028911da7b38ef311a21ba1bb41bcea204066bfc74ddad6` |

No product source, tests, contracts, or repository state were edited by this
packet. Unrelated dirty paths are excluded: benchmark result artifacts,
comparison report, and other `.agents` material. The source was hash checked
before and after packet assembly; see [SHA256SUMS](SHA256SUMS).

## Real callers and shared context

The script's point loop invokes the benchmark binary for `read`, `write`,
`query`, and `mixed` at lines 43-62. Its auto-index call reaches
`bench/src/main.rs` dispatch (`--family auto-index --target terrabase`) and
then `bench/src/auto_index_local.rs:29-33`, which reads
`TERRABASE_BENCH_TERRABASE_API`, `..._DATABASE`, and `..._AUTH` and sends the
first authenticated request. Its ingest call reaches
`bench/src/main.rs` (`--ingest-target terrabase`) and
`bench/src/ingest.rs:353-372`, which requires the endpoint, health endpoint,
authorization, version, and artifact digest before measuring.

The accepted design is to derive the bearer value from the verified member key
inside each fresh `new_database` lane, preserving fresh database selection and
avoiding broad environment copying. Relevant unchanged source is available at
`bench/src/main.rs`, `bench/src/auto_index_local.rs`, `bench/src/ingest.rs`,
`bench/FAMILIES.md`, and `bench/RESULTS.md` in the reviewed working state.

## Checks and benchmark evidence

| Evidence | Command/source | Result and source identity |
| --- | --- | --- |
| Shell syntax | `bash -n bench/scripts/run-terrabase-final.sh` | exit 0, current script hash above |
| Patch mechanics | `git diff --check` | exit 0 |
| Point run | `bench/results/run-20260907-independent-1714381/point.log` | exit statuses: read 1 (HTTP 429 during warmup), write 1 (response timeout), query 124 (timeout), mixed 1 (query response timeout); receipts retained and failed |
| Point run envelope | `.../environment.txt`, `.../member-point-after.txt` | candidate commit `fae32d99...`; member `2ocpu/12GiB`, cpuset 0-1, memory max 12884901888; run group `e13-local-20260908T0125Z` |
| Failed receipt identity | `.../terrabase-{read,write,mixed}-*.json` | all use 5 samples / 10,000 ops / C16 / 1,000 warmup, but `status=failed`, no samples, and no observed runtime identity |

The full final orchestrator result, ingest, auto-index, and sync evidence is
pending after the worker handoff to the benchmark owner. Prior diagnostic
receipts under `bench/results/run-20260907/` are stale/non-qualifying and are
not reused as current passing evidence. No focused harness reproduction beyond
syntax and diff checks was supplied; this is a review limit.

## Interactions and review priorities

Review the environment contract at the two unchanged consumers, fresh database
ordering, fail-closed credential/version precondition, and whether the ingest
variables remain scoped to the ingest database. Confirm that the final runner's
artifact digest and member process/resource evidence still flow unchanged.

No correctness verdict is made here. Astra should inspect the complete diff and
the linked callers, then state any required regression or runtime evidence.

## Reading order

1. [harness-fix.diff](harness-fix.diff)
2. `bench/scripts/run-terrabase-final.sh` around `new_database`, point loop, and ingest setup
3. `bench/src/auto_index_local.rs:29-45` and `bench/src/ingest.rs:353-390`
4. `bench/src/main.rs:54-117` and the evidence paths above
