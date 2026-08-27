# Typed replication observation operations decision record (GAP-R03, ticket #80)

Status: **proposed** — written for parent ratification. Direction was
parent-decided by the gap adjudication (the PS-04 transfer-adapters sweep's
follow-up 3: "Expose
conflicts/fenced/depositions/denials/cursor/health as typed TBEP operations
with redaction rules", folding D09); the operation vocabulary, the tenant
gate, the content restrictions, and the wire registration are this record's
to propose. Every changed path in this packet is inside its write scope and
reversible.

Owner: GAP-R03 (#80), parity row PB-5035
(`replication-commands-and-snapshots`, partial). Coordinates SE-14 (#10,
the TBEP registry this record registers into) and SE-08 (#11, the C# host
whose engine-backed `TbepHost` wiring is the recorded integrator action).
GAP-R02's follow-up 2 — "typed operator surface for watermark transitions
(floor advance, bound declaration, refusal log) — natural rider on
GAP-R03's typed replication observation operations" — is **not** taken
here: the watermark's transitions are mutations of retention state, not
observations, and folding them into an observation ticket would blur the
read-only discipline this record proposes. It stays a proposed follow-up.

## Problem

The replication data plane keeps six bounded, observable fact logs per
member — conflicts, fenced batches, depositions, tenant-isolation
denials, the journal-derived cursor, and the health derivation — and
every one of them is reachable only as a crate-private accessor
(`ReplicationMember::conflicts()`/`fenced()`/`depositions()`/`denials()`/
`cursor()`/`health()`). The PS-04 sweep recorded the consequence on
PB-5035: "No typed host operation exposes them yet (no
GetConflicts/progress-operations surface)."

An operator (or an agent driving the backend) therefore cannot ask the
replication plane a single one of its own maintenance questions: which
conflicts were recorded and how they resolved, which leadership
transitions deposed this member, which below-epoch pushes it fenced,
where a tenant's replication stands, or whether the member is healthy —
not without linking the engine crate and reaching into crate-private
state, which no external surface may.

V1 shipped this surface (its shape is the parity evidence for this
record):

- A paged maintenance operation for conflicts, invoked from the client
  and served by a typed server command: `GetConflictsOperation(start,
  pageSize)` at
  `test/TerraBase.SlowTests/Sharding/Client/Operations/GetDocumentConflictsTests.cs:59`
  (pinned V1 tree
  `/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly`),
  command classes at
  `src/TerraBase.Client/Documents/Commands/GetConflictsCommand.cs:9` and
  `src/TerraBase.Server/Documents/Commands/Replication/GetConflictsOperation.cs:51`.
- Replication task progress as an operator-visible observation:
  `GetExternalReplicationTaskProgressShouldWork` and its
  `VerifyReplicationProgressAsync` driver at
  `test/TerraBase.SlowTests/Server/Documents/OngoingTasks/ReplicationOngoingTaskProgressTests.cs:40,57`.

One V1 property must NOT carry over. V1's conflict result carries the
conflicting document bodies themselves — `GetConflictsResult.Conflict`
holds `BlittableJsonReaderObject Doc`
(`src/TerraBase.Client/Documents/Commands/GetConflictsResult.cs:18-33`).
V2's observation surface is deliberately narrower (decision D2 below):
conflict *facts* are observable, conflict *contents* are not.

## Vocabulary

- **Observation operation** — one typed, read-only acquisition of a
  member's bounded observable facts: `observe_conflicts`,
  `observe_fenced_history`, `observe_depositions`, `observe_denials`,
  `observe_cursor`, `observe_health`
  (`src/replication/observation.rs`).
- **Tenant claim** — the untrusted tenant an observation request names,
  exactly the `tenant_claim` discipline of the typed operation registry
  (`src/operations.rs`). The authorized truth is the caller's
  [`TenantScope`](`crate::types::TenantScope`); a claim the scope does
  not name is refused typed before any fact is read.
- **Observation refusal** — the typed failure of an observation
  operation: `ObservationRefusal::CrossTenant { scope, attempted }`
  (mirroring `MemberError::CrossTenant` at the data-plane seams) and
  `ObservationRefusal::EngineClosed` / `JournalUnreadable` for the
  cursor's engine facts.
- **Content-free by construction** — no observation type has a field,
  accessor, or rendering path that can carry a document body, a field
  value, or a collection's payload. Identities, epochs, sequences,
  clocks, counts, and verdicts only — the same construction rule as the
  retained deletion evidence (GAP-R02) and the content-free trigger
  events (PD-2026-08-26-13 D3).

## D1 — Every observation runs under a scope and refuses any other tenant

Every observation operation takes the caller's `TenantScope` and the
request's tenant claim, and the first act of each is the same gate:

```text
scope.tenant() == tenant_claim   or   ObservationRefusal::CrossTenant
```

This is the engine's `TenantScope` discipline mirrored onto the
observation seam, with the data plane's own refusal as the template
(`serve_catch_up` denies a request naming another tenant with
`MemberError::CrossTenant`, recorded in the denial log). The gate is
uniform across all six operations — including the member-plane facts
(fenced history, depositions, health) that carry no per-tenant data —
because the discipline that is easiest to audit is the one without
exceptions: no observation of any kind answers a tenant the scope does
not name.

Tenant filtering follows the claim, not the log: the conflicts
observation returns only the conflicts recorded for the claimed
tenant's keys (which is why `ConflictRecord` gains a `tenant` field —
see D3), the cursor observation returns the claimed tenant's position
on the member's journal, and the denials observation returns the
denials recorded against the claimed tenant's scope.

## D2 — Content restrictions: facts observable, contents never

The redaction rule the ticket names is a type-level construction, not a
filter applied at the door:

- Every observation result type is built from identities (`MemberId`,
  `TenantId`, `OperationId`), topology text (data-range names), epochs,
  `StorageSequence` values, D-3 clock stamps, typed verdicts
  (`ConflictOutcome`, `ReplicationStatus`, `DenialSeam`), and counts.
  There is no field of type `Document`, `DocumentValue`, `TbepValue`,
  `bytes`, or any recursive value type, and no accessor that produces
  one — the compile-fail proofs in `src/replication/observation.rs`
  pin the absent paths the same way `src/replication/mod.rs` pins the
  absent acknowledgement and convergence paths.
- The bound is the observation *window*, never the observation *depth*:
  the underlying logs are already bounded (`MAX_CONFLICTS`,
  `MAX_OBSERVATION_LOGS`, `MAX_DENIALS`), so an observation is a copy
  of a bounded set, not a traversal of history.
- V1 parity difference, stated not hidden: V1's `GetConflicts` returns
  the conflicting documents (`GetConflictsResult.Conflict.Doc`); V2's
  conflicts observation returns the recorded policy outcome with the
  conflicting operation identities, writers, and clocks. A caller who
  needs the losing write's content re-issues it through the document
  plane under its own scope. Proposed WS-PARITY disposition:
  implemented-at-seam with this accepted difference (identity-level
  conflict observability, content-free).

## D3 — `ConflictRecord` gains its tenant

A conflict is a fact about a key, and a key belongs to a tenant. Until
this record the conflict record carried only the operation identities,
writers, and clocks — enough to *record* the conflict, not enough to
*scope* its observation. The record now carries the tenant it was
recorded under, filled at all four recording sites (the three D-3
dispositions in `resolve_incoming` and the deposed-owner retention in
the apply-time fencing path), which is what the D1 gate filters on. The
field is an identity; it adds no content surface.

## D4 — The subject is the member; the operations are crate-internal; the wire is registered

- The subject of every observation is a `ReplicationMember`, which is
  crate-private by design (the lane harness drives it; production
  membership lifecycle is Platform/Orchestrator-owned). The observation
  operations are therefore `pub(crate)` free functions over
  `(&ReplicationMember, &TenantScope, &TenantId)`, driven by in-crate
  tests exactly like the data-plane seams they observe.
- The **result vocabulary is public**: `terrabase_v2::replication::*`
  re-exports the observation result types and the refusal, and promotes
  the three observable record types (`FencedBatch`, `Deposition`,
  `TenantDenial` with `DenialSeam`) from crate-private to public, so an
  engine-side adapter can map them without re-deriving member state.
  The crate root's re-export list is untouched (integrator-owned).
- **TBEP registration**: the registry has a natural slot — the
  `0x0130` topology/admin block, which already carries the two
  control-plane reads (`topology.read` 304, `admin.status` 305). Six
  new non-mutation operations register at 306..=311 with responses at
  562..=567, family `replication`, in a new budget class `observation`
  (see D5). No request carries a tenant (PD-2026-08-26-11 D1): the
  bound channel's tenant is the scope, and the engine-side adapter
  derives the claim from it. Host and CLI consumption are out of scope;
  the adapter surface proposals below record the mapping.
- Fail-closed dispatch: the shipped hosts do not serve the new kinds,
  so the server's dispatch answers them with the typed
  `operation_refused` (never a misleading `malformed_frame`) until the
  engine-backed host integrator action lands.

## D5 — The observation wire budget

The `observation` budget class is 65,536 wire bytes per frame. The
derivation (worst-case widths, the same reasoning the
`operation_large` class uses): a conflict record at maximal identity
widths (tenant 64, two operation identities at 128, two member
identities at 128, two stamps, an outcome with its member) is ~754
bytes; the bounded log it comes from holds at most 64 records, so a
maximal conflicts response sits near 48.3 KB plus the fixed envelope —
inside the class with margin, with every other observation strictly
smaller. The transport bounds member and range identity strings at 128
bytes (the engine's logical-identifier rule allows 255;
`src/replication/identifier.rs:16`): a wider member identifier is
observable in-process but not over TBEP — a disclosed wire limit this
record states rather than hides.

## Rejected alternatives

- **Redaction at the door** (observe everything, strip bodies in the
  adapter): rejected — a filter can be forgotten, a missing field
  cannot. The content restriction must be a construction property of
  the observation types themselves (D2).
- **One aggregate "member status" operation**: rejected — V1's surface
  separates conflicts from progress, and an aggregate would force a
  single budget class over the union of all fact shapes while making
  partial observability impossible to refuse individually.
- **Observations as `pub` methods on the member**: rejected — the
  member is crate-private and stays crate-private; free functions over
  the member keep the acquisition seam in one module with one gate.
- **Tenant-free observation of member-plane facts** (fencing, health):
  rejected in D1 — a uniform gate is auditable; a per-operation
  exemption is a standing temptation.

## Limits

- The observation surface reads process-local state: the bounded logs
  are in-memory (the cursor and the retained evidence are the durable
  facts), so a restarted member observes an empty conflict, fencing,
  and deposition history until the plane records new facts. The R-2
  record's limits already state this for the logs themselves.
- No cross-member or cross-Region aggregation exists here: each
  observation names one member. Cross-Region aggregation stays with the
  qualification lane owners (PB-5042..PB-5047, SE-07/SE-12).
- The TBEP registration ships the wire slots and the typed refusal; it
  does not serve observations from a live engine process. The
  engine-backed `TbepHost` mapping is the integrator action recorded
  below.
- No performance claim of any kind: observations are typed reads of
  bounded sets; nothing here is timed, and nothing may be cited as
  fast or slow.

## Adapter surface proposals (integrator actions, not taken here)

1. **Engine-backed `TbepHost` mapping** (extends SE-14's named action 3):
   serve `replication.conflicts|fenced|depositions|cursor|health|denials`
   by calling the observation operations under the bound channel's
   tenant scope, mapping `ObservationRefusal::CrossTenant` to
   `operation_refused` with detail naming the isolation refusal (the
   bound-channel tenant makes the refusal unreachable in practice — it
   is the defense in depth the record keeps typed).
2. **C# client surface** (SE-08 rider): one request/response pair per
   registered operation over the existing `TerraBase.Protocol` client;
   the generated registry (both languages) already carries the shapes.
3. **CLI/studio read-only views** (SE-13/DB riders): conflicts and
   progress commands rendering the observation results — reads only,
   no mutation surface.

## Evidence

- Red observations: `src/replication/observation_tests.rs` failed
  against the pre-packet tree with the module absent (exact command
  and compiler output in the packet report); the TBEP-side red is the
  frame-kind absence in `crates/terrabase-tbep`.
- Green receipts: the workspace gates (`cargo fmt --all -- --check`,
  pinned `cargo +1.97.1 clippy --offline --locked --all-targets --
  -D warnings`, `cargo test --offline --locked`,
  `cargo test --workspace --offline --locked`, `scripts/check.sh`) and
  the `terrabase-tbep` crate suite run from the crate directory
  (drift, corpus, adversarial, UDS) — recorded verbatim in the packet
  report.
- WS-PARITY proposals: `docs/parity/gapr03-ws-parity-proposals.md`
  (proposals only; no overlay was mutated).
