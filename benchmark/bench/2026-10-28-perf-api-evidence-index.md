# API/auth/SDK overhead evidence, 2026-10-28

The objective is incomplete. The original server-over-engine overhead target of
<10% at c=1/16 is unmet; authenticated SDK/raw parity, all-region normal rollout
and retained synthetic-account cleanup have not been proved. No denominator is
changed to call this done. Local hosting attribution excludes socket/header parsing
and includes instrumentation; process CPU/allocations also include client/background work.

| Area | Established result | Evidence and limit |
| --- | --- | --- |
| Per-request key watch | Three quiet same-host pairs; small c=16 client p50 median 190 → 175.5 µs (−7.6%), nonoverlapping ranges | [Key-watch comparison](2026-10-28-perf-api-keywatch.md); other latency ranges overlap; no general p99 claim |
| Key-watch mechanism | Cancellation-exception events fell from 1,432,978 / 1,432,943 requests to 0 / 1,470,731 | Same report; separate diagnostic observer, not the latency comparison |
| .NET SDK transport | c=16 SDK changed from H1/16 connections to H2/one connection; quiet SDK/raw-H2 p50 ratios 0.980 at c1 and 0.967 at c16 | [HTTPS control](2026-10-28-perf-api-https-merged.md); anonymous JWKS, overlapping latency ranges, no consistent speedup |
| Claims candidate | Rejected: lower allocation but medium c1 p50/CPU regression | [All 32 cells](2026-10-28-perf-api-claims.md), 24 eligible; unapplied patch only |
| Token-hash candidate | Rejected: lower allocation but mixed/regressing latency | [All 56 cells](2026-10-28-perf-api-token-hash.md), 24 eligible; unapplied patch only |
| CPU investigation | 87,022 owned-process CPU events with stacks; usage accounting ranks prominently among selected frames | [Diagnostic ranking](2026-10-28-perf-api-cpu-ranking.md); one perturbed capture, no latency or method-self-CPU claim |
| Regional Go attempt | Rejected: empty H2 window; no SDK matrix completed | [Live rejection and cleanup history](2026-10-28-perf-api-go-live-rejected.md) |
| Node/Python/Rust runners | Offline controls qualified; no accepted authenticated regional parity result | [Node](2026-10-28-perf-api-node-window.md), [Python](2026-10-28-perf-api-python-lifecycle.md), [Rust](2026-10-28-rust-api-parity-output.md); Rust SDK transport observations remain null |

The production key-watch change is [#795](https://github.com/TerraScale/terrascale/pull/795).
Its per-request admission, 250 ms watch poll, two-second send-time positive-cache
freshness, Until deadlines and fail-closed refusal are preserved. Auth/revocation,
metering, FULL commits and local acknowledgements were not weakened.
The .NET SDK transport change is [ts-dotnet #100](https://github.com/TerraScale/ts-dotnet/pull/100).
Merged source and local measurements do not prove either change is serving everywhere.

## Retained serving observations

Authenticated internal health through admitted ts-orch NetBird SSH, 07:41:11–18 UTC:

| Region | Serving node | Revision | Health |
| --- | --- | --- | --- |
| gru | ts-gru-2 | `dc414b8a281aeb8d03a53f310e1a2a138350a6c7` | ok |
| sjc | ts-sjc-1 | `8f398b3291551da11fe3dc0da2de17ead5e165b5` | ok |
| fra | ts-fra-1 | `f9404808aa8a25d22c7a72a342d9988579e37482` | ok |

All report storage format `18-e15d70682919`; sjc predates #795. These process
responses establish mixed serving revisions, not latency or a complete rollout.
The [sanitized raw snapshot](2026-10-28-perf-api-serving-0741.json) retains timestamps.
The [auth inventory/readiness snapshot](2026-10-28-perf-api-auth-0741.json), 07:41:22–30 UTC,
shows gru health/readiness 200 on its unchanged image, and no running auth task in
the checked namespace on sjc/fra host aliases. That inventory does not establish
those regions' public auth routing or availability.

A [serving recheck](2026-10-28-perf-api-serving-0817.json), 08:17:08–16 UTC,
returned the same three revisions and storage format. The corresponding
[auth recheck](2026-10-28-perf-api-auth-0817.json) returned the same gru image,
health/readiness 200, and checked sjc/fra task inventory. These unchanged
observations do not establish recovery of the receipt's storage read failure.

The normal retry [40643893257](https://github.com/TerraScale/terrascale/actions/runs/40643893257)
terminated with failed [auth release](https://github.com/TerraScale/terrascale/actions/runs/40643893257/job/122668231687)
and [TerraBase release](https://github.com/TerraScale/terrascale/actions/runs/40643893257/job/122668232315).
Each release log contains three `connect: nats: Authorization Violation` refusals.
This is the observed release error, not a proven underlying credential cause.
Later benchmark-only main successes do not prove rollout. No workflow was changed
and no manual deployment or serving-data reset was performed.

## Remaining proof and cleanup

Run b's private recovery directory `/tmp/api-overhead-26w5i0s8` remains on beta.
Its last two receipt-only reads, 06:57:52 and 06:58:12 UTC, returned 503 before
any deletion continuation; readiness had already recovered by then. They correlated
with an auth-to-TerraBase unavailable failure. No unchanged-state retry or replacement
tenant is justified by the later unchanged readiness/revision observations.
The [historical receipt-log diagnostic](2026-10-28-perf-api-receipt-diagnostic.md)
preserves that correlation while keeping the underlying refusal cause unproven.

After authoritative storage/receipt recovery, reconcile that tenant first. Before
creating another tenant, preflight the current runners, credentials, output paths
and a fresh admitted TS_PEERS_FILE snapshot. Then run three quiet same-host repeats
and retain authenticated gru/sjc/fra results, actual serving revisions, complete
latency windows and cleanup/reconciliation outcomes. Unknown SDK attempt/protocol
data must stay null; logical database deletion does not prove remote erasure.
