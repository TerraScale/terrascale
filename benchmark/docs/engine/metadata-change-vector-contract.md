# Document metadata and change-vector contract decision record (GAP-D02, ticket #59)

Status: **accepted** — ratified by the parent at integration, 2026-08-27,
on review of D1-D4 against ADR-0004 D-3, the rejected alternatives, both
gate verdicts (inspector GO with four NOTEs, warden PASS), and the warden's
fresh receipts (658 tests green, full check.sh EXIT 0, STRICT V1
QUALIFICATION PASSED). The warden's two MINORs are landed by the same
integrator commit that flips this status: the `Ord`/`PartialOrd` derives are
dropped from `DocumentVersionContract` so the type enforces D1's
no-ordering rule instead of merely documenting it, and the fail-closed
parse bounds are pinned by tests. This record owns the V1→V2 change-identity
mapping, the exists/HEAD read contract, the conditional-load (If-Match)
contract, and the migration-mapping artifact the V1→V2 logical migration
(#44) consumes.
The implementation in this packet lands behind this record; every changed
path is inside this packet's write scope (`src/engine/metadata.rs`,
`src/engine/metadata_tests.rs`, `src/engine/mod.rs` declarations,
`tests/engine/document_metadata_contract.rs`) and is reversible.

Owner: GAP-D02 (#59), which folds GAP-D03 (exists/HEAD, conditional load)
and GAP-R5 (V1 change-vector mapping for migration) per parent
adjudication PD-2026-08-26-14 D3.

## Problem

V1 identified a document's change with a **change vector**: a per-database
etag list such as `A:5-AAAAAAAAAAAAAAAAAAAAAA, B:12-BBBBBBBBBBBBBBBBBBBBBB`,
carried in `@metadata` under `@change-vector` beside `@last-modified`
(wall-clock ticks), `@flags` (including the new-document marker), and a
session-level ignore-changes marker (pinned V1 commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee`:
`src/TerraBase.Client/Constants.cs` `Metadata` block;
`src/TerraBase.Server/Documents/Replication/ChangeVectorParser.cs`;
`ChangeVectorExtensions.cs`; `ChangeVectorEntry.cs`). V1's read surface
used that identity three ways: HEAD returns 404 for a missing key, 304 when
the supplied vector still matches, and 200 plus the current vector's ETag
otherwise (`DocumentHandlerProcessorForHead.cs`); a conditional GET with
the caller's etag answers an empty result when the vector is unchanged and
the document otherwise (`DocumentHandlerProcessorForGet.cs`,
`TryGetCachedPlainDocumentResult`); and replication compares vectors
per-database to decide conflict outcomes.

V2 had no answer at the read seam: `DocumentMetadata` carried only the
published version, existence could only be inferred by materializing a
document, no conditional read existed, and PS-01/PS-04 left PB-2004
(metadata), PB-2006 (exists/HEAD), PB-2007 (conditional load), and the
PB-5021-adjacent mapping rows blocked on exactly this decision.

## D1 — What a V1 change vector becomes in V2

**Decision.** A V1 change vector — of any arity, the ticket's five-entry
vector of a five-database V1 topology included — becomes **no live V2
identity at all**. The live V2 change identity of a document is the
`DocumentVersionContract` triple in `src/engine/metadata.rs`:

| V1 fact | V2 contract component | Authority |
| --- | --- | --- |
| `@change-vector` (the whole per-database etag list) | *nothing live* — replaced by the triple below | this decision |
| which change of this document the caller holds | `version: u64` — the per-key monotone publication version | engine publication (`DocumentMetadata::version`) |
| (no V1 fact) | `sequence: StorageSequence` — the global publication sequence that produced this version | engine publication history |
| the vector's per-database merge order | `stamp: ReplicationClockStamp` — the D-3 hybrid-logical-clock stamp | ADR-0004 D-3, the replication lane |

Equality over the whole triple is the only comparison the read seam
performs. The `sequence` component is load-bearing: V2 restarts the
per-key version at one after a delete/re-create cycle (storage `apply`:
`root.rs` — `map_or(1, |existing| existing + 1)` on a fresh key), so
`version` alone cannot distinguish the two version-one publications of one
key; `sequence` does, and the ABA test
(`conditional_load_rejects_the_recreated_document_stale_vector`) pins it.
(`src/storage/root.rs`, `apply`: a put of a key the table does not hold
starts at version one; an existing key's version increments.)

**D-3 respect.** The stamp component exists so the contract is the one
typed shape both planes project onto: the engine core publishes without a
replication stamp, so engine-local history is served at the lane's **origin
clock `(0, 0)`** — the same deterministic constant the replication
transport uses for unstamped history (`src/replication/transport.rs`,
`StampedCommit::at_origin_clock`). The stamp never varies with the
reader's clock (the alternative the replication lane uses for *advisory*
catch-up service, "serve at the server's current clock", would make the
contract of one publication drift between reads and break equality — the
exact defect the contract exists to prevent). Ordering authority stays
where ADR-0004 D-3 put it: the `(stamp, Member)` total order in the
replication lane. No V1 etag is laundered into a stamp.

**Rejected alternatives.**

- **Etag translation** — map a V1 entry's etag into the V2 version, or the
  dominant etag into the stamp counter. Rejected: the V2 version is a
  per-key publication counter that V2 assigns (migration is a fresh V2
  publication); importing a foreign, unbounded counter collides with the
  counter space V2 mints (`version 1` of a migrated document must mean
  "first V2 publication", not "V1 etag 12"), and a stamp counter is a
  within-tick sequence, not an etag — the translation would corrupt both
  sides' meanings and silently violate D-3's ordering authority.
- **Vector preservation as a live multi-writer causality set** — store the
  full V1 vector in V2 metadata and have HEAD/conditional-load compare
  vector strings, keeping V1's comparison basis. Rejected: V2 convergence
  is the `(stamp, Member)` total order (ADR-0004 D-3), so a preserved
  per-database vector would be a second, divergent comparison basis at the
  same seam — two answers to "which change wins". It also contradicts the
  accepted surface decision PD-2026-08-26-10 (exactly one service surface;
  no V1 wire compatibility) by keeping V1's wire identity alive inside V2.
- **Drop with a count** — discard V1 vectors during migration, disclosing
  only a count. Rejected for migration *provenance*: the vector is the V1
  document's change identity, and #44's manifest-verified contract records
  transformations rather than losing them silently. The recorded mapping
  below keeps the audit trail content-free and bounded instead.

## D2 — Exists/HEAD over the contract

`Transaction::head(collection, id, condition)` answers V1's HEAD semantics
typed:

- `HeadCondition::Any` → `DocumentHead::Missing` (V1 404) or
  `DocumentHead::Current(contract)` (V1 200 + ETag).
- `HeadCondition::IfUnchanged(contract)` → `DocumentHead::Unchanged`
  (V1 304) on exact triple equality, `DocumentHead::Current` otherwise —
  a stale supplied vector is never silently matched.

Existence resolves by the exact storage key (tenant, collection,
identifier), and the read observes **only published state**: staged writes
of the calling transaction are invisible because they have no published
identity until commit (pinned by
`head_reads_the_published_view_not_staged_writes`).

## D3 — Conditional load (If-Match) over the same contract

`Transaction::load_if_match(collection, id, expected)` loads the document
only when the stored contract equals `expected`. Every other outcome is a
typed rejection in `ConditionalLoadError`:

- `Missing` — an If-Match precondition on a missing key cannot hold (the
  write path's `ExpectedVersion::Exact` treats a missing key the same way:
  a mismatch, never an empty success).
- `Stale { current }` — the supplied vector is stale; the rejection carries
  the current contract so the caller can re-issue against it.
- `Engine(EngineError)` — the contract cannot be served at all; the read
  fails closed instead of guessing an identity.

This is deliberately If-Match (reject-on-mismatch) rather than V1's
conditional-GET cache shape (serve-empty-when-unchanged): the cache shape
is a client/session concern (PB-2021's request cache) and the engine seam
owns the stronger, simpler precondition form; a caller composes the
HEAD/304 behavior from D2 when it wants the cache shape.

## D4 — The rest of V1 `@metadata`

| V1 fact | V2 disposition |
| --- | --- |
| `@last-modified` (wall clock) | Not a contract component and never a convergence or comparison input (D-3 orders on the hybrid logical clock, not wall time). Migration carries it as ordinary document content under #44's content policy; V2 assigns no wall-clock change identity. |
| new-document marker (`@flags`/session) | The write path's `ExpectedVersion::New` (require-absent) is the engine-side marker; already implemented and proven (`expected_version_any_new_and_exact_are_enforced`). |
| ignore-changes marker (session) | The write path's `ExpectedVersion::Any` (no concurrency check) is the engine-side analog; same proof. |
| `@metadata` dictionary as a whole | `DocumentMetadata` carries exactly the version; V2 has no user-writable metadata dictionary at the engine seam. Remaining V1 keys are family-owned (`@attachments`, `@counters`, `@timeseries`) or content (`TerraBase-Clr-Type`) under #44's per-kind dispositions. |

## The migration mapping for #44 (BK-03)

The artifact is typed and tested, ready for #44 to consume:
`V1ChangeVector::parse` parses V1's exact grammar (little-endian base-26
node tags — `A`=0, `AB`=26 — decimal etags, exactly 22-character database
identities, comma/whitespace separator runs, trailing separator accepted),
and `V1ChangeVectorMapping::from_vector` reduces a parsed vector to:

- `entry_count` — the disclosed arity;
- `dominant` — the greatest-etag entry with the database identity as
  tie-break, V1's own etag order, recorded as **provenance only**;
- `provenance_digest` — an order-canonical FNV-1a fold over the
  canonically sorted entries (the storage layer's digest family; audit
  identity, explicitly not cryptographic).

The migrated document itself receives a fresh V2 identity from its first
V2 publication through the Engine transaction seam (version one, the
migration commit's sequence, origin stamp) — exactly what #44's existing
document conversion already does. **Integrator action** (this packet's
write scope excludes `src/migration/`): wire
`V1ChangeVectorMapping::from_vector(&V1ChangeVector::parse(...)?)` into
#44's document conversion so each migrated document's report entry carries
the recorded provenance, and replace the module-doc sentence "the
V1→V2 change-vector and metadata mapping … is owned by GAP-D02 (#59)" with
a citation of this record. Nothing in the mapping blocks #44: the current
behavior (no V1 identity preserved) is exactly decision D1's live-identity
half; the wiring only adds the recorded provenance.

Two parser differences from V1 fail closed where V1 computes silently, and
are part of this decision: an etag past the `u64` range rejects
(`EtagOverflow`) instead of wrapping V1's signed accumulator, and a
non-digit inside an etag rejects instead of mis-reading the character code.
Inputs are bounded (4096 bytes, 64 entries) so the typed parse is bounded
deterministic; wider V1 vectors fail closed rather than parse.

## Limits

- **History cost**: the contract's sequence comes from a backward walk of
  the tenant's publication history (`tenant_sequence` +
  one-commit pages, in-memory journal index) to the last commit touching
  the exact key. Worst case one walk per HEAD/conditional load over the
  tenant's whole history; bounded by the same journal the replication
  catch-up and backup readers page. No performance claim is made here —
  any latency statement needs a Benchmark Receipt under
  PD-2026-08-26-03.
- **Pruned history fails closed**: if the publication history can no
  longer prove a document's publication (no tenant commit, an index/journal
  mismatch, or a table/journal version disagreement), the read returns
  `ConditionalLoadError::Engine(EngineError::StorageFailure)` rather than
  an unprovable identity — the same fail-closed rule as repair F5. When
  GAP-R02 (#79) lands a retention watermark, its bound must respect this
  read.
- **Origin stamp**: engine-local history is served at `(0, 0)` by
  construction. When the replication lane stamps publications, the stamp
  component becomes the lane's recorded fact; until then the component is
  a stable constant, and no V1 etag ever occupies it.
- **Delete/re-create**: `version` restarts at one after a delete (existing
  storage behavior, unchanged here); the contract stays unique through the
  sequence component. A future tombstone-aware HEAD (distinguishing
  "deleted" from "never existed", PB-2002/GAP-D09) would extend — not
  reinterpret — `DocumentHead`.

## Run evidence (this packet)

Commands run from the packet worktree root on branch `fleet/gapd02-2`:

- Red (before implementation): `cargo test --offline --locked --test engine`
  — 8 unresolved-import errors plus 5 private-constructor errors on the
  not-yet-existing contract surface (the intended red).
- Green: `cargo test --offline --locked --lib engine::metadata` —
  10 passed; `cargo test --offline --locked --test engine` — 12 passed
  (9 new contract tests + 3 pre-existing public-construction tests).
- Full finish-protocol receipts (fmt, pinned clippy, check, test suite,
  `scripts/check.sh`) are recorded in the packet report at integration.
