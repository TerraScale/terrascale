# GAP-R03 typed observation receipts (ticket #80)

Lane: focused in-crate and crate-level runs in this worktree
(`fleet/gapr03-2`). No exclusive recovery-lane or benchmark-lane run:
GAP-R03 holds no exclusive-lane flag, changes no recovery, durability, or
storage-format path, and adds no performance-relevant behavior — the
observation operations are typed reads of bounded in-memory sets. No
timing was taken and no performance claim is made anywhere in the packet.

Scope of the claims below: **implemented, focused-test-proved, lane-scoped
only.** Row dispositions stay with WS-PARITY; the proposals are recorded
in `docs/parity/gapr03-ws-parity-proposals.md`.

## Decision record

`docs/replication/gapr03-replication-observations.md` (proposed for
parent ratification): the uniform scope gate with the typed cross-tenant
refusal (D1), the content restrictions as a construction property with
the stated V1 difference (D2), `ConflictRecord` gaining its tenant (D3),
the crate-internal operations with public result vocabulary and the TBEP
registration in the `0x0130` block (D4), and the observation wire budget
(D5), with four rejected alternatives.

## Engine suite (`src/replication/observation_tests.rs`, 6 tests)

| Test | Fact proved |
| --- | --- |
| `cross_tenant_observation_probes_refuse_typed_at_every_operation` | all six operations refuse a tenant the scope does not name, both directions of the mismatch, before any fact is read |
| `conflicts_observation_is_tenant_scoped_names_the_outcome_and_stays_content_free` | the recorded policy outcome is observable for the claimed tenant; another tenant's matching observation carries none of it; canary documents never appear |
| `fenced_history_and_depositions_are_observable_as_typed_operations` | the below-epoch push is fenced with range/epochs/source observable; the deposition names held epoch, owner epoch, new owner; content-free |
| `cursor_observation_reports_the_claimed_tenants_position_and_refuses_typed_when_closed` | per-tenant positions from the journal; an empty tenant observes `None`; a closed member refuses `EngineClosed` |
| `health_observation_reports_the_member_status_and_never_a_convergence_fact` | Assigned → Degraded (denial) → Unavailable (closed) through the typed operation |
| `denials_observation_reports_the_isolation_log_of_the_claimed_tenants_scope` | the denial log is observable for the claimed tenant's scope with seam, attempted tenant, counterparty; another tenant's observation is empty |

Compile-fail proofs in `src/replication/observation.rs` (doctests): no
`documents()` accessor exists on `ConflictsObservation` or
`CursorObservation` — the content restriction is a construction property.

## TBEP suite (`crates/terrabase-tbep/tests/replication_observation_frames.rs`, 5 tests)

| Test | Fact proved |
| --- | --- |
| `the_observation_operations_register_in_the_topology_admin_block` | codes 306..=311 with responses 562..=567, family `replication`, class `observation` (65,536 bytes), read-family idempotency pairing |
| `observation_requests_refuse_an_idempotency_key` | sealing a conflicts request with a key fails `idempotency_pairing` |
| `observation_bodies_round_trip_content_free` | the maximal 64-record conflicts response byte-exact round-trips inside the written budget |
| `the_remaining_observation_bodies_round_trip_their_typed_facts` | fenced/depositions/cursor/health/denials bodies round-trip their typed facts (cursor both present and absent) |
| `observation_requests_are_refused_typed_until_a_host_serves_them` | on a bound channel over a real UDS socket, all six requests answer the typed `operation_refused`, never a direction violation |

## Verification commands (this worktree, at the packet tip)

- `cargo fmt --all -- --check` — exit 0
- `env -u RUSTUP_TOOLCHAIN cargo +1.97.1 clippy --offline --locked
  --all-targets -- -D warnings` — exit 0
- `env -u RUSTUP_TOOLCHAIN cargo test --offline --locked` — exit 0,
  943 passed / 0 failed
- `env -u RUSTUP_TOOLCHAIN cargo test --workspace --offline --locked` —
  exit 0, 1148 passed / 0 failed
- `scripts/check.sh` with `TERRABASE_V1_ROOT` and
  `TERRASCALE_WORKSPACE_ROOT` set — passed
- `crates/terrabase-tbep`: `cargo test --offline --locked` from the crate
  directory — exit 0 (68 passed / 0 failed, 2 ignored regen rows; drift,
  corpus, adversarial, UDS, observation frames) and pinned
  `cargo +1.97.1 clippy --offline --locked --all-targets -- -D warnings`
  — exit 0
- `host/`: `dotnet run --project TerraBase.Protocol.Tests` — 19/19 green
  with the regenerated registry and corpus; `dotnet build
  TerraBase.Protocol.csproj` — 0 warnings, 0 errors

## Deviation note

`evidence/` is gitignored (W4-CA-12, integrator-owned): this receipt is
written within the lease and left untracked, exactly as the GAP-R02
receipt was. The durable copies of the claims are the decision record,
the proposals doc, and the committed tests.
