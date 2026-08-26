---
status: accepted
---

# ADR 0010: Auto-indexing and the query optimizer

Parent record of charter PD-2026-08-26-09, 2026-08-26. Status:
**accepted** — the parent accepted this ADR at Phase-1 integration
(2026-08-26) on review of the full text against PD-2026-08-26-09 D1-D4,
the AX-1..AX-26 extraction evidence, and the inspector/warden gate
records for IX-01. D-3's explicit-index-candidate divergence from V1
AX-11 is charter-mandated and accepted as recorded.

Owner: the IX wave (extraction and dispositions
`docs/indexing/v1-auto-index-extraction.md`,
`docs/indexing/dispositions.md`; wave
`specs/2026-08-26-terrabase-v2-auto-index-tickets.md`).

Evidence base: V1 behavior extracted at pinned commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee`
(`docs/indexing/v1-auto-index-extraction.md`, inventory AX-1..AX-26) and
per-behavior dispositions (`docs/indexing/dispositions.md`).

## Context

Charter PD-2026-08-26-09 requires zero-configuration indexing (D1), an
automatic auto-index lifecycle (D2), a measured optimizer (D3), and
construction on the accepted W3-2 query/index seam and the engine-owned
catalog (D4, charter follow-up CA-3). V1 proves the behavior is
workable: a dynamic query that no index serves creates an `Auto/` index
deterministically named after its field set; partial matches are widened
into covering indexes; superseded and unused auto indexes are deleted
without user action. V1 also shows the gaps V2 must close: selection
reasoning is only observable through a debug flag, and dynamic queries
never consider explicitly created indexes at all.

The engine today already has the substrate: a query seam with typed
staleness (`StalenessMode::Strict`/`AllowStale`, `GenerationView` over
observed/available generations, generation-used reporting per
namespace), and — landed as the W3-2 CA-3 follow-up through integrator
action W4-CA-3 — an engine-owned `IndexCatalog` (`src/index/mod.rs`)
whose namespaces carry epochs and monotonic generations, whose rebuilds
follow the compare-and-publish rule (`RebuildOutcome::RejectedAsStale`),
and whose publication sink the `Engine` registers at open, making index
maintenance production-live.

## Decisions

### D-1 — every selection records its reasoning (Index Selection Record)

Index selection is a measured, recorded act, not a hidden heuristic.
Every dynamic query produces an Index Selection Record: the query
identity, the candidate set considered, each candidate's verdict
(coverage, capability sufficiency, state), the tie-break rules applied,
the chosen index (or the creation event), whether the plan came from the
plan cache, and the staleness basis of the answer. The charter's
selection-quality rule is enforced mechanically: no query may be served
by a worse-than-available index without the reason being present in its
record. Records are bounded engine state (retained per the
observability/config budget, not unbounded), are queryable through a
typed operation, and are MCP-exposable under the standard rules
(PD-2026-08-26-07). V1 parity hook: this strengthens AX-14/AX-15, where
reasons exist but only on demand (`?debug=explain`).

### D-2 — automatic creation on the unmatched query

When a dynamic query has no fully serving index, the optimizer derives
an auto-index definition from the query shape (where and order-by
fields, per-field capabilities, group-by/aggregation structure), names
it deterministically from the canonical (collection, field set,
capabilities) tuple under the reserved `Auto/` namespace, and creates it
in the engine-owned catalog without user action. Because the name
encodes the field set, definition drift that does not change identity
updates in place under one identity, while a strictly wider field set
has a different name and takes the supersede path (D-4) — V1's AX-9
split. Creation is fail-closed: a query needing a capability whose
backing configuration is absent errors rather than creating a wrong
index (V1's vector guard pattern, AX-6). The optimizer kill-switch
remains an engine configuration that fails closed with a typed error
(AX-3).

### D-3 — explicit indexes are optimizer candidates and take precedence

Unlike V1 (AX-11), dynamic queries consider explicit indexes as
candidates. When an explicit index equivalently serves the query, it is
preferred over selecting or creating an auto-index; auto-index creation
happens only when no explicit index serves. A query that names an index
bypasses the optimizer entirely (V1 AX-26 parity). The `Auto/` namespace
stays reserved so explicit and automatic indexes can never collide, and
dynamic queries never mutate or rename an explicit index. This is the
charter's "explicit indexes take precedence where equivalent" made
concrete, and it prevents V1's failure mode of auto indexes duplicating
equivalent static ones.

### D-4 — lifecycle is automatic and hooks the engine-owned catalog

The auto-index lifecycle runs entirely against the `IndexCatalog`,
with no user action and no external scheduler:

- **Supersede on the query path:** a partial match widens the mapping
  (field and capability union; group-by fields are never invented) and
  creates the covering index; the superseded index is deleted only after
  the replacement's coverage reaches it, after a drain wait. A query
  whose index is replaced mid-execution retries bounded, never
  interrupting the query (V1 AX-9/AX-17).
- **Background surpass pass:** engine maintenance re-runs the matcher
  index-vs-index, deleting surpassed indexes and replacing partially
  surpassed ones one extension per pass (V1 AX-18).
- **Idle and retirement:** every query marks its index queried; an
  auto index unqueried past the idle threshold goes idle (still
  selectable, demoted in preference, woken synchronously on query);
  still-unqueried idle indexes are deleted past the deletion threshold.
  Transitions record the measured value and threshold, as V1's observer
  reasons do, and no transition issues while reporting state is
  incomplete (V1's fail-closed observer rule, AX-21).
- **Durability and correctness floor:** auto-index state derives from
  storage publication order through the catalog's epoch/generation
  machinery; deletion or widening never weakens durability or returns
  incorrect results, and zero-configuration never bypasses the
  compare-and-publish monotonicity rules.

### D-5 — staleness contract unchanged, waits bounded

The optimizer adds no staleness semantics. Queries observe the existing
staleness contract (stale by default; non-stale waits only when
requested); a freshly created auto index applies a bounded default
wait (V1 AX-23/AX-24 behavior). Durations are engine configuration
recorded at implementation and validated by measurement, never turned
into performance claims outside PD-2026-08-26-03.

### D-6 — selection quality is measured (benchmark-mandatory)

Optimizer objectives — selection quality (no worse-than-available index
without recorded reason), auto-index overhead, and end-to-end query
performance — are proven with Benchmark Receipts under
PD-2026-08-26-03 against the TerraBase V1 and PostgreSQL baselines in
the exclusive benchmark lane. "Best query times" remains an objective,
not a claim; the Index Selection Record (D-1) is the audit surface that
makes selection quality checkable at all.

## Rejected alternatives

### R-1 — manual-only indexes

Require users to create every index (the InstantDB/Convex model).
Rejected: it violates charter PD-2026-08-26-09 D1 ("no query ever
requires the user to create an index"), abandons proven V1 parity
behavior (PB-4045/PB-4046 are core rows with critical risk), and gives
up a recorded competitive differentiator (CP-01: auto-indexing exceeds
both competitors). It also moves a mechanical planning problem onto
every client.

### R-2 — an optimizer without observability

Reproduce V1's planner behavior but keep selection reasoning internal
(explain-as-debug only, no records). Rejected: charter D3 makes measured
selection quality a requirement, and "no query on a worse-than-available
index without a recorded reason" is unverifiable without D-1's records.
An unobservable optimizer also cannot support the Operations Studio's
index views (Live View parity consumer) nor agent-facing diagnosis, and
regressions in selection would be invisible until customers felt them.

### R-3 — a Platform-owned or externally scheduled optimizer

Place index selection, creation, and lifecycle outside the engine (a
control-plane service or external scheduler driving the database).
Rejected: this repository owns database behavior (AGENTS.md
engine/Platform separation); index selection sits on the query hot path,
and PD-2026-08-26-05's native-placement rule pushes external hops out of
engine hot paths; lifecycle decisions (supersede, retire) must observe
catalog truth (epochs, generations, coverage) that only the engine
holds. An external optimizer would also reintroduce a configuration
surface for what the charter mandates be zero-configuration.

## Consequences

- The IX wave owns implementation and evidence for these decisions; the
  plan cache, the record retention bound, the exact `Auto/` name format,
  and the idle/delete defaults are recorded in the implementation
  packets with tests at the query/index seam.
- PB-4045/PB-4046 disposition proposals in
  `docs/indexing/dispositions.md` follow from D-2/D-3/D-4; WS-PARITY
  owns the actual row dispositions.
- Two V1 behaviors are consciously not carried forward as-is: dynamic
  queries considering only auto indexes (D-3) and debug-only selection
  explanations (D-1). Both are recorded as adaptations, not silent
  drift.
- No performance claim is made or implied by this ADR; D-6 governs any
  future claim.
