# ADR 0024 sync corpus coverage audit and expansion

Status: evidence record for the ADR 0024 corpus-coverage packet,
2026-08-29. Authority: ADR 0024
(`docs/adr/0024-reactive-query-sync-state-machine.md`), the workspace
charter, and the SE-05 conformance kit
(`crates/terrabase-sync-conformance/`).

## Scope and method

The packet audited the `terrascale.sync.v2` golden-vector corpus
against the corpus-coverage states ADR 0024 requires, then added the
missing states using the native codecs only (`terrabase-native/1`
deltas, `terrabase-snapshot/1` snapshots). The audit read:

- the corpus interpreter (`crates/terrabase-sync-conformance/src/corpus.rs`,
  the step vocabulary and the checks each step makes);
- every committed base vector
  (`crates/terrabase-sync-core/vectors/terrascale.sync.v2/*.json`,
  category, description, and step list);
- the base runner's own category mapping
  (`crates/terrabase-sync-core/tests/vectors_runner.rs`, which records
  the deferred categories explicitly);
- ADR 0024's required coverage list (the "Loro replacement" section:
  snapshot, delta, duplicate, gap, ack, reconnect, reset,
  authorization change, slow consumer, route change, malformed input;
  plus "Required evidence" for the qualification lanes).

The audit is of the corpus at commit `d67c4c4` ("Migrate sync core and
corpus to native state"), the native-codec corpus generation.

## Per-state coverage table (before this packet)

| ADR 0024 state | Base corpus vector | Coverage verdict |
| --- | --- | --- |
| Snapshot handoff (bootstrap: setup, acceptance, full-state answer, acknowledgement) | `fresh-session` (session_setup → session_accepted → snapshot_answer, `serve_snapshot` byte-exact, checkpoint_ack) | Covered at the delivery-substrate level |
| Result deltas | `repeated-change-delta`, `slow-consumer`, `checkpoint-resume` (`serve_delta` byte-exact, budgeted batches) | Covered at the delivery-substrate level (whole-document per-field put/remove operations in `terrabase-native/1`) |
| Result patches / view-level result operations (Insert, Update, Patch, Move, Remove, top-k) | none | **Absent** — no protocol expression exists yet (see remaining gaps) |
| Duplicate delta delivery | `reconnect-replay` (byte-identical re-delivery, different correlation, same content) | Covered |
| Gap (checkpoint behind the served revision) | `checkpoint-resume` (delta and gap), `slow-consumer` (sequential catch-up) | Covered at the delivery-substrate level |
| Checkpoint acknowledgement | `fresh-session`, `checkpoint-resume`, `reconnect-replay` (checkpoint_ack frames; retransmission bounded by the acknowledged checkpoint) | Covered for the session-level sync checkpoint |
| Reconnect resume | `reconnect-replay` | Covered (same-Region reconnect from a checkpoint) |
| Reset (behind retained floor → resync snapshot) | `resync-required` (resync_required frame, snapshot adoption, both resync reasons recorded as frames) | Covered |
| **Authorization-generation change / reset** | none | **Absent** — the only ADR 0024 corpus state with no vector at all; the base runner's header records it as deferred pending engine-side machinery |
| Slow-client coalescing | `slow-consumer` (budgeted, coalesced multi-commit batches under a byte budget) | Partially covered — the typed resumable overload close (ADR 0024 slow-client ladder step 3) had no vector |
| Route change | `region-route-change` | Covered |
| Malformed input | `malformed-frame-rejection` (version, unknown kind, truncation, trailing bytes, length disagreement, idempotency pairing) | Covered |

The two absent/partial states are exactly what this packet added.

## What this packet added

### New corpus vectors (native codecs only)

Owned by `terrabase-sync-conformance` under
`crates/terrabase-sync-conformance/vectors/terrascale.sync.v2-adr0024/`,
corpus identity `terrascale.sync.v2-adr0024`, schema
`terrascale.sync.v2/vector-corpus@2` (the same file identity the base
corpus carries), gated by the same manifest machinery:

1. **`authorization-reset`** (`authorization-reset.json`, 9221 bytes,
   sha256 `525a333dc7cfbc35b04bf44f556a22c6769e9c2ef015a639b64ad7f39ca6fa4a`).
   ADR 0024's authorization-generation change/reset state, pinned in
   the wire frames that exist today: a live authorized session is
   served its delta suffix (byte-exact `serve_delta` against the real
   core, plus the budget recount); the grant over the scope is revoked
   and the server closes the session with the typed
   `error{scope_refused}` frame; the reconnect presents the stale
   checkpoint (`session_setup{resume}`) and is answered by a fresh
   authorized bootstrap — `session_accepted` at the server's current
   checkpoint plus a full `snapshot_answer` (`serve_snapshot`
   byte-exact), never a delta replay from the revoked position — and
   the client acknowledges the new position (`checkpoint_ack`).
2. **`slow-consumer-overload`** (`slow-consumer-overload.json`, 16674
   bytes, sha256 `4de22ee93905d76e4dbffcfdf0e9d0cc5921942d3a300d8d3ac9801d3ada7e3a`).
   The tail of ADR 0024's slow-client ladder: the whole backlog is
   coalesced into one byte-budgeted native delta batch (with the
   payload/overhead, per-delivered-change, and delta-below-snapshot
   budget recounts); the non-acknowledging consumer is closed with the
   typed resumable `error{overloaded}` result; the client reconnects
   from its last acknowledged checkpoint and the server serves exactly
   the suffix after it (byte-exact `serve_delta`), then the client
   acknowledges the drained position.

Both vectors were generated by the committed regenerator
(`tests/generate_extension_corpus.rs`, run explicitly via
`cargo test -p terrabase-sync-conformance --test generate_extension_corpus -- --ignored`)
from typed scenario definitions against the real `SyncCore` and
protocol codec — the same deliberate-regeneration discipline the base
corpus's SE-04 runner holds. The committed corpus is the golden
record: the normal kit tests replay it byte-exact.

### Extended manifest and harness machinery

- The corpus integrity manifest moved to schema
  `terrascale.sync.v2/corpus-manifest@2`: each entry names the corpus
  (directory identity) its file belongs to, so one manifest gates a
  multi-corpus kit run. The legacy `@1` form still parses (its entries
  belong to the base corpus) and is never emitted.
  `fixtures/manifest.json` now covers all 11 vectors; the nine base
  entries are byte-identical to the previous committed manifest (no
  base corpus file was touched).
- `ConformanceHarness` executes a corpus list with per-corpus coverage
  roles: the base corpus is gated on the nine `REQUIRED_CATEGORIES`,
  the extension corpus on the two new `EXTENSION_CATEGORIES`
  (`authorization-reset`, `slow-consumer-overload`).
  `ConformanceHarness::kit()` is the repository reference run; the
  binary's default run is the kit, and `--corpus` is repeatable.
- New failure-detection coverage: a corrupted extension wire byte, a
  missing extension category, a tampered extension file against the
  manifest gate, and a manifest entry naming a corpus the run does not
  hold all fail closed.

## Reference run (the receipt)

Commands and outcomes, run on the packet's final source state:

```text
cargo test -p terrabase-sync-conformance
    27 tests passed, 0 failed, 1 ignored (the deliberate regenerator)
cargo clippy -p terrabase-sync-conformance --all-targets
    0 warnings
cargo fmt -p terrabase-sync-conformance -- --check
    clean
cargo test --test architecture engine_core_and_native_codecs_are_loro_free
    passed (the extension corpus and generator introduce no Loro reference)
cargo run -q -p terrabase-sync-conformance -- \
  --manifest crates/terrabase-sync-conformance/fixtures/manifest.json \
  --report conformance-report.json
    exit 0 — 11/11 vectors passed, 320/320 checks, manifest verified
```

Per-vector check counts of the new vectors in the manifest-gated run:
`authorization-reset` 27 checks, `slow-consumer-overload` 36 checks.
The base corpus alone still passes 9/9 vectors, 248/248 checks
(ungated), unchanged by this packet.

## Honest remaining gaps

These are ADR 0024 coverage states the corpus still cannot express,
because the engine-side machinery does not exist yet. They are
recorded here, not papered over:

1. **View-level result operations.** ADR 0024's `ResultDelta`
   operations (Insert/Update/Patch with canonical path
   encoding/Remove/Move, identity tie-breaking, top-k entry and
   eviction) have no protocol or codec expression. The delivery
   substrate carries whole-document per-field put/remove operations in
   `terrabase-native/1`. No vector can cover result patches, moves, or
   top-k behavior until the ADR 0024 view state machine lands (the
   base runner records the same deferral).
2. **The ADR 0024 `ViewCheckpoint`.** Authenticated-encryption view
   checkpoints (view ULID, authorization generation, retention
   generation, expiry, key rotation) are not implemented. The corpus
   checkpoints are the session-level sync checkpoint (per-peer ends
   plus storage sequence). Checkpoint expiry, key rotation, tampering,
   cross-tenant replay, and excessive-token-size vectors from ADR
   0024's required-evidence list cannot exist yet.
3. **Authorization-generation evaluation itself.** The
   `authorization-reset` vector pins the wire behavior (typed scope
   refusal, refused stale resume, fresh-snapshot re-bootstrap), not
   per-document visibility removal, field-projection reduction, or
   disconnect-during-revocation — those need the engine-side
   authorization dependency evaluation of the view state machine.
   The delivery substrate's authorization surface is the scope (one
   tenant's collection), which is exactly what the vector exercises.
4. **Cross-Region resume decisions.** `region-route-change` covers the
   route-change frame; the retained-delta / bounded-reconstruction /
   reset decision across Regions is not exercised by any vector (no
   multi-Region harness exists at this seam; ADR 0024's GRU/SFO/FRA
   qualification lanes own it).
5. **Qualification lanes.** ADR 0024's required-evidence items that
   are deterministic-simulation or measurement lanes — a commit between
   dependency registration and snapshot completion, one hundred
   thousand subscriptions under measured tenant isolation, API process
   loss, and malformed private gRPC input are outside a corpus of golden
   vectors by nature and remain unclaimed. The retired public transports
   are not qualification requirements.

## Non-claims

- No base corpus file was modified; the SE-04 runner's byte-exactness
  gate is untouched and green.
- The byte counts recorded in the new vectors are deterministic
  artifact values, not measured performance (PD-2026-08-26-13 D1:
  measured budgets are SE-07/SE-15's).
- "Implemented" here means: code and focused tests exist and pass for
  the coverage states the wire protocol can express today. It is not a
  claim that the ADR 0024 view state machine, its checkpoints, or its
  qualification lanes are implemented or qualified.
