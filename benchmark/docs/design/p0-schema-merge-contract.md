# P0 contract: CRDT-aware schema modes, merge rules, delete-wins, shadow delete

> Superseded Loro-era implementation contract. Schema-free, validated, and
> strict modes remain useful inputs, but native CRDT kinds, scalar defaults,
> deletion, quarantine, collection, and coordinated invariants now follow ADR
> 0019 and the workspace `GOAL.md`.

Status: frozen by the P0 contract pass, 2026-08-28. Governs P2/P3
implementation and AC-10 merge scenarios. Parents: ADR 0014
(D-2/D-3/D-6/D-7), charter PD-2026-08-28-03. Builds on the SM-02 schema
format (`docs/families/schemas.md`, `src/families/schemas.rs`) and the
ratified counter merge encoding
(`docs/families/counter-merge-encoding.md`).

## 1. Collection schema modes

Every collection selects exactly one mode, recorded in collection
metadata, default `schema-free`:

| Mode | Declared paths | Undeclared paths |
| --- | --- | --- |
| `schema-free` | none required | accepted; stable CRDT kind recorded at first write |
| `validated` | versioned rules enforced | accepted |
| `strict` | versioned rules enforced | rejected with a typed error |

- A declaration (in `validated`/`strict`) fixes BOTH the value domain
  (SM-02 rule set) and the CRDT kind at each governed path.
- `schema-free` still records a stable CRDT kind per path once created:
  a later write of an incompatible kind at the same path is rejected
  (fail closed), because silently re-kinding a path breaks convergence.
  The concurrent-CRDT-kind-mismatch policy for `schema-free` paths is a
  recorded open decision (charter unresolved list): today it fails
  closed to quarantine, and any relaxation needs a parent decision.

## 2. Validation points (both required)

1. **Admission**: before durable acceptance, the coordinating Member
   validates the mutation against the collection's named schema
   version. Invalid -> typed rejection; nothing durable.
2. **After merge**: after any CRDT merge (replication, anti-entropy,
   read-repair merge, snapshot install), the materialized document is
   re-validated. A schema-invalid merged state is quarantined from
   reads and indexes and emits an auditable failure receipt
   (`SchemaQuarantine`). It never becomes silently visible.

## 3. Merge-safe vs coordinated rules

- **Merge-safe** (closed under merge): type/shape rules, enum
  membership, number range, text length — the SM-02 rule set. These run
  coordination-free at admission; single-document CRDT operations stay
  available during a partition.
- **Coordinated** (not closed under merge): cross-field constraints,
  uniqueness, compare-exchange, and any rule whose truth depends on
  other documents or non-merged state. These run through the
  request-scoped coordinated transaction path (leaderless per ADR
  0018 D-6): quorum-persisted transaction state, any active Member may
  recover an abandoned transaction, refusal while the quorum is
  unavailable. The API reports which class an operation used — never
  pretending coordination-free availability for a coordinated rule.

## 4. Parallel merge semantics (per ADR 0014 D-6)

- Distinct document paths merge independently.
- Maps merge per key. Lists and text use Loro's ordered CRDT semantics.
- Counters merge additively (ratified PN-counter bucket encoding).
- Concurrent same-path scalar assignments select ONE deterministic
  visible winner via Loro's causal and operation ordering — never
  wall-clock arrival, never server-local timing. Losing values persist
  in retention-governed shadow history. The public document never
  exposes an unresolved multi-value register.

## 5. Delete-wins

- A concurrent delete + update resolves DELETE WINS: the document is
  deleted. Delete-wins takes precedence over every §4 rule.
- The delete is a tombstone (shadow delete, §6), not an erasure.

## 6. Shadow delete and shadow history

- Enabled by default. The tombstone hides the document while the losing
  concurrent state remains recoverable in shadow history.
- Retention: 30 days by default, configurable per collection
  (`shadow_retention_days`). GC requires ALL of: retention expiry,
  causal stability, and every snapshot, backup, replication, sync, and
  recovery pin released.
- Storage pressure triggers alerts or backpressure — never unsafe early
  deletion.
- An explicit restore creates a NEW document incarnation (new document
  causal identity referencing the shadow source); it never removes the
  original tombstone from history and never resurrects the old identity.

## 7. Schema migrations (CRDT-aware)

- Versioned, resumable, idempotent (SM-03 substrate). Old writers
  either negotiate a compatible schema version or fail closed.
- A migration transforms documents under an explicit recorded contract;
  its result is validated at the target version (§2 applies post-merge
  as post-transform).
- A migration that would produce quarantinable state stops, records the
  receipt, and leaves the source version active.

## 8. Honest limits

- The current tree enforces SM-02 write-path rules but has no
  three-mode model, no post-merge validation, no quarantine, no shadow
  history, and no delete-wins tombstones at the document engine level;
  those are P2/P3 work and every scenario here is `unresolved` until
  then.
- Classification of specific coordinated rules, the `schema-free`
  concurrent-kind-mismatch policy, and default read consistency remain
  open decisions with owners.
