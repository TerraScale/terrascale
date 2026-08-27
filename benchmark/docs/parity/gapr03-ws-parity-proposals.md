# GAP-R03 WS-PARITY proposals (ticket #80)

Proposals only, per the PS-wave rule that builders never flip overlays: the
dispositions below are for WS-PARITY (SE-12 close-out, #14) to record from
this packet's evidence. No overlay was mutated; the parity tree is untouched
by this packet.

Evidence base: decision record
`docs/replication/gapr03-replication-observations.md`, the observation
suite `src/replication/observation_tests.rs`, and the TBEP-side suite
`crates/terrabase-tbep/tests/replication_observation_frames.rs`.

## PB-5035 `replication-commands-and-snapshots` — propose `implemented-at-seam` (was partial)

The PS-04 sweep's finding was that bounded observations exist as crate
accessors (`conflicts()`/`fenced()`/`depositions()`/`denials()`/`cursor()`/
`health()`) but "no typed host operation exposes them yet". They are now
exposed as typed operations at both halves of the seam:

- Engine: `src/replication/observation.rs` — six typed observation
  operations over `(&ReplicationMember, &TenantScope, &TenantId)` with one
  uniform typed cross-tenant refusal (`ObservationRefusal::CrossTenant`,
  mirroring `MemberError::CrossTenant`), public result vocabulary
  (`ConflictsObservation`, `FencedHistoryObservation`,
  `DepositionsObservation`, `DenialsObservation`, `CursorObservation`,
  `HealthObservation`), and compile-fail proofs that no document body can
  leave the surface.
- Wire: the TBEP registry's `0x0130` block carries `replication.conflicts`
  (306→562), `replication.fenced` (307→563), `replication.depositions`
  (308→564), `replication.cursor` (309→565), `replication.health`
  (310→566), and `replication.denials` (311→567) in the new `observation`
  budget class, with regenerated bindings on both sides and the drift
  contract green. An unserved request on a bound channel is answered with
  the typed `operation_refused`.

Why `implemented-at-seam` and not `implemented`: serving the frames from a
live engine process (the engine-backed `TbepHost` mapping) is the recorded
integrator action — the registry slots and the engine operations exist on
either side of it, and neither half is reachable through a shipped host
process yet. The V1 progress-operations parity anchor
(`ReplicationOngoingTaskProgressTests`) is covered in shape, not in
deployment.

## Accepted difference stated for the record (PB-5022 rider)

V1's `GetConflicts` returns the conflicting document bodies
(`GetConflictsResult.Conflict.Doc`,
`src/TerraBase.Client/Documents/Commands/GetConflictsResult.cs:18-33` in the
pinned V1 tree). V2's conflicts observation is content-free by
construction: identities, clocks, and the recorded policy outcome only. A
caller who needs a losing write's content re-issues it through the document
plane under its own scope. This is the ticket's "redaction rules" realized
as a type-level construction rather than a strip-at-the-door filter; the
WS-PARITY disposition for PB-5022 should name it when that row closes.

## Coordination rows (no disposition proposed here)

| Row | Relation to this packet |
| --- | --- |
| PB-5022 `replication-conflict-resolution` | The conflicts observation is the operator window onto this row's recorded outcomes; `ConflictRecord` now carries its tenant, which the observation gate filters on. |
| PB-5031 `replication-local-progress` / PB-5033 `replication-restart-and-rejoin` | The cursor observation exposes the journal-derived per-tenant position these rows describe; the wire op is `replication.cursor`. |
| PB-5032 `replication-health-and-convergence-observations` | `replication.health` carries the member `ReplicationStatus`; health still never implies convergence (the compile-fail proofs stand). |
| GAP-R02 follow-up 2 (watermark transitions as typed operations) | Deliberately not folded here: watermark transitions are mutations of retention state, not observations. It stays a proposed follow-up for the parent to file. |
| SE-08 (#11) / SE-13 | The adapter surface proposals in the decision record name the C# client and CLI riders; nothing in `host/` or `cli/` changed beyond the regenerated `TbepRegistry.g.cs`. |

## Proposed follow-ups (for the parent to file or fold)

1. The engine-backed `TbepHost` mapping (decision record D4, integrator
   action): serve the six registered frames by calling the observation
   operations under the bound channel's tenant scope.
2. Cross-member/cross-Region aggregation of observations (SE-07/SE-12
   lane owners): each observation names exactly one member today.
3. Persisting the bounded observation logs (aligns with W4-CA-13's
   persisted fencing state): a restarted member currently observes empty
   in-memory logs.
