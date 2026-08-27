# Storage compaction decision record (GAP-S1, ticket #81)

Status: **accepted for this packet** — the capability, bounds, crash-safety
ordering, and retention integration decided here are implemented by this
packet's code and proved by its tests and recovery receipt. The residual
deferred items (named under "Limits") carry their own owners.

Owner: GAP-S1 (#81), closing the storage-seam residual the charter
registered after W3-1 ("full-table roots with O(n) commits and no
compaction; snapshot registry/root history growth", GOAL.md ledger
2026-08-24 03:00) and the follow-ups the GAP-R02 watermark record named
this ticket for: the durable retained-evidence store, the production
history-bound declaration, and the recovery-lane receipts for compaction.

Coordinates: GAP-R02 (#79, `docs/replication/tombstone-retention-watermark.md`
— D2's rules are quoted as gates below), BK-01/BK-02 (backup/restore
composition), SH-03 (the journal-divergence consumer this record's gate
protects), GAP-S2 (#82, key management — untouched).

## Problem

Three durable artifacts of a V2 location grow without bound, and nothing
can honestly give any of them up:

1. **Root files.** Every `Written`/`Synced` commit writes a full-table
   root file; superseded roots are never removed.
2. **The write-ahead log.** Every durable commit appends prepare/data/
   commit-marker frames; the log is never shortened. Recovery only needs
   the frames of commits newer than the published root, but they stay
   forever anyway.
3. **The publication-history journal.** The servable window only ever
   extends. GAP-R02 built the watermark that says *when* history may be
   given up, but no engine component may drive it: its durability rule
   (`EvidenceNotDurable`) fails closed precisely because no durable
   retained-evidence store exists.

The operation ledger is deliberately **not** on this list: it is the
replay-identity anchor (D6 below) and compaction never rewrites it.

## D1 — In-band, caller-driven maintenance (the online/offline decision)

**Decision: compaction is an in-band maintenance pass on a live, open
Engine — `Engine::compact(CompactionRequest)` — exactly like the IX-03
`maintain_indexes` rider: a typed, synchronous, host-scheduled capability,
not a background task and not an offline tool.**

- *Offline rewrite* (a tool that rewrites a closed location) is rejected:
  it would create a second implementation of the format, a second recovery
  path, and an operational state ("being compacted") the Engine cannot
  see. The V2 model is one open Engine per location; the pass runs under
  the same storage lock every commit takes, so the ordering discipline
  below is the only crash-safety story needed.
- *Background/online-with-traffic* (a thread compacting while commits
  proceed) is rejected: the engine core is synchronous and runtime-neutral
  (ADR 0005); there is no concurrency story to build on, and the
  publication order's invariants are proven single-threaded. A pass holds
  the storage lock for its duration; commits before and after it are
  unaffected, and a kill during the pass is equivalent to a kill between
  two commits.
- *Triggering*: there is **no automatic trigger** in this packet. The pass
  runs when a caller asks (the host/Platform owns cadence, the same
  PD-2026-08-26-01 D1 split `maintain_indexes` uses). An automatic
  threshold would need a policy surface (byte budget, interval) nobody has
  specified; the capability lands first.

## D2 — What one pass may rewrite, and what it never touches

A pass is one of two policies ([`CompactionPolicy`]):

- **`Reclaim`** — the always-safe phases:
  1. *Checkpoint*: write and sync the published root file, publish and
     sync the manifest naming it, sync the directory. After this step the
     published state is durable independent of the WAL.
  2. *WAL reclaim*: truncate the write-ahead log to zero and sync it.
     Every commit the frames covered is at or below the checkpointed
     manifest sequence, so recovery can never need them again.
  3. *Root reclaim*: delete root files whose sequence is strictly below
     the manifest sequence. The manifest's own root and any root at or
     above the manifest sequence (roll-forward partners of an interrupted
     commit) are always kept, so at least one verifying root always
     remains on disk (D7 depends on this).
- **`ReclaimHistory`** — everything `Reclaim` does, plus giving up
  publication history below the journal's durable head, under the gates of
  D5. The bound is always the journaled high-water itself: the window
  given up is `[servable start, head)`, and the head record stays servable,
  so `servable_history_start()` after a pass names the head exactly.

**Never rewritten**: the `format` file; the operation ledger (D6); the
manifest's own root file; the retained-evidence store's verified prefix
(the store is append-only; a torn trailing record is truncated at open
exactly like the journal's, repair-F1 discipline).

## D3 — The durable retained-evidence store (the GAP-R02 deliverable)

A new durable artifact in the location: the `retained` file, an
append-only, FNV-1a-checksummed record stream in the same framing as the
journal (`u32` length, `u64` checksum, payload). Two record kinds:

- *Fact* — one content-free deletion fact: tenant, collection (flagged
  absent for the provable no-op delete), document id, version at delete,
  journal sequence, operation identity, achieved local durability. No
  document content, ever (a tombstone that kept the body would keep
  deleted data alive).
- *Bound* — the durable declaration: the bound, the journal head it was
  declared against, and the per-tenant highest non-deletion sequence
  below the bound (`non_deletion_through`, the D4 fact of the GAP-R02
  record — it must be extracted before the journal records are dropped,
  because afterwards the puts below the bound are gone).

Damage discipline: a torn or checksum-failing **trailing** record is an
interrupted append — truncated at open. The same damage **mid-file** fails
the open with `FormatMismatch`: the store backs every below-bound
declaration, and silently serving a prefix of the evidence would be
delayed data loss, the exact defect GAP-R02 exists to prevent. The
torn-tail reading is bounded by a maximum legal record size (facts and
bound records have a small bounded payload): a length header declaring
more than any legal record could carry cannot be an interrupted append —
interrupted appends only ever produce prefixes of legal frames — so it
is integrity damage wherever it sits, and the open fails closed instead
of silently truncating every record behind a corrupted length field. A
frame declaring a legal length whose payload is cut off by end of file
keeps the torn-tail reading.

Stamps: D-3 clock stamps are process-local lane state (GAP-R02's recorded
residual); the store does not persist them. Evidence re-derived from the
store carries the origin (zero) stamp — the same disclosed degradation
`StampedCommit` documents for post-restart history.

Facts are deduplicated on append by full identity (tenant, id, sequence,
operation), so a pass retried after a kill between the store append and
the journal rewrite cannot grow the store.

## D4 — Crash safety: the kill-point discipline

The pass is a linear sequence with injected one-shot fault points inside
it (the same `FaultPoint` machinery the publication order uses). The
invariant every kill point must preserve: **reopening the location loses
no document, no resolution, and never presents a below-bound consumer a
gap the retained evidence does not cover.**

| Kill point (fault) | State on disk | Why reopening is safe |
| --- | --- | --- |
| `CompactionCheckpointPublish` (manifest not published) | Root file written and synced; manifest still names the older root | The new root is a superset state a later commit or pass reuses; the manifest-verifies path ignores it |
| `CompactionWalTruncate` (before truncation) | Checkpoint synced; WAL full | Ordinary pre-compaction state |
| `CompactionEvidenceSync` (store appended, not synced) | Torn store tail; journal intact | Store truncated to its verified prefix at open; no bound is durable; the journal still serves the window |
| `CompactionJournalRewrite` (store synced, journal not rewritten) | Store holds facts + bound; journal still serves below the bound | Redundant-but-consistent: evidence is durable *and* the journal still covers the window; the effective bound composes by max; the next pass is idempotent (dedup) |
| `CompactionRootReclaim` (before deletion) | All roots present | Deletion of strictly-older roots is the last phase and never removes the manifest root |

The two load-bearing orderings, stated as rules:

1. **Checkpoint before WAL truncation.** The WAL is only shortened after
   the root file, manifest, and directory entries are synced, so the
   roll-forward input the frames provided is replaced by a strictly more
   durable checkpoint.
2. **Evidence before history.** The retained store is appended and synced
   (file and directory) *before* the journal is rewritten. This is
   GAP-R02 D2.3 ("durability before discard") enforced as an ordering
   rather than a convention: a crash between the two leaves evidence
   durable and history redundantly present — never the reverse.

The journal rewrite itself is atomic at the file level: the retained
records (a leading high-water mark preserving the pre-pass head, then the
kept records in sequence order) are written to `history.tmp`, synced,
renamed over `history`, and the directory is synced. A kill during it
leaves either the old or the new journal, both consistent; a stale
`history.tmp` is ignored at open and overwritten by the next pass.

Idempotency: every phase either re-does identical work (checkpoint files
are content-addressed; the store dedups; the journal rewrite converges to
the same retained set) or discloses a refusal. Repeated passes are safe.

## D5 — Interaction with servable history and the retention watermark

The pass honors GAP-R02's D2 rules as hard gates:

- **Window must fully exist (D2.4):** the bound is the journal's own
  durable head, never beyond it.
- **Readable window (D2.2):** history reclaim requires a servable window
  (`servable_history_start()` is `Some`). A journal whose first record is
  torn serves nothing; giving up "history" then would be declaring a
  window whose facts were never read. Refused and disclosed.
- **Divergence gate (this record's addition):** history reclaim is refused
  when any operation the ledger resolves as committed below the candidate
  bound has no journal record (`journal_divergence`, restricted to
  sequences below the bound). A diverged commit's deletion evidence was
  never journaled, so no extraction can recover it; giving up the window
  anyway would lose that tombstone permanently. The refusal is disclosed
  with the count; the checkpoint/WAL/root phases still run. Sequences
  below the store's durably declared bound are exempt from the count:
  a prior pass already extracted their evidence (that is what the bound
  records), so their absence from the journal is compaction, not
  divergence — without the exemption a pass retried after a kill between
  the journal rewrite and completion could never converge.
- **Extraction before discard (D2.2):** facts and per-tenant
  `non_deletion_through` are extracted from the verified journal window
  and made durable (D3) before the rewrite.
- **Retention floor respected:** a pass driven with a watermark handle
  reads the watermark's floor first and never discards what the watermark
  holds — facts at or above the floor are always written to the store;
  facts below it are skipped with a disclosed count (the same
  `below_floor_discarded` vocabulary GAP-R02 uses), because the watermark
  itself discards below-floor facts when the floor advances.
- **Durability before discard (D2.3):** the ordering of D4; the
  declaration to the watermark is made with `durably_held = true` only
  after the store is synced.

**The production declaration path.** `Engine::compact_with_retention`
drives the watermark GAP-R02 left un-driven:

1. *Prologue (re-derivation):* the pass first reads the durable store and
   re-seeds the handed watermark — `hold()` of every durable fact, plus a
   store-backed bound declaration restoring `non_deletion_through`. This
   is the restart story GAP-R02 recorded as impossible ("the retained
   evidence is the journal's while the journal covers it") made possible
   by the store.
2. *Declaration:* after the pass reclaims history, the engine declares the
   new bound from the window it just extracted, with
   `durably_held = true` backed by the synced store.

A new watermark form supports step 1:
`TombstoneRetention::declare_history_bound_retained` — the same D2.4,
monotone, and durability gates as the windowed form, with the
non-deletion facts taken from the store's bound record instead of a
journal window (after compaction there is no window to read). It is the
store-backed analogue GAP-R02's Limits said GAP-S1 must provide.

Servable-history composition is unchanged: the effective bound for a
consumer remains `max(engine servable_history_start, watermark declared
bound)` — compaction raises the first term physically and the second by
declaration, never lowers either. The rewritten journal opens with a
high-water mark record carrying the pre-pass head, so
`journaled_high_water()` never regresses across a pass and the
divergence signal stays meaningful.

## D6 — Interaction with the operation ledger's replay identity

The ledger is never rewritten. Consequently:

- `resolve_commit` answers identically before and after a pass; a
  same-identity same-write-set retry replays the recorded resolution and
  a different write set under the same identity is `OperationIdReuse`
  (tested).
- The sequence-allocation watermark survives WAL truncation because it is
  re-derived at open from the manifest and the ledger's `Ready`/
  `Published` records, which name every allocated sequence that reached
  a ledger record.
- **Disclosed consequence — the Buffered tail.** A checkpoint writes the
  root file for the *published* table, which includes `Buffered` commits
  (in-memory by design). A `Buffered` commit checkpointed by a pass
  therefore becomes durable, and its durable `Ready` record — whose root
  identity now equals the published root — resolves `Committed` after
  the next reopen instead of `Pending` forever. The reported level stays
  the requested one (`resolution_for` caps every claim at what the
  operation asked for), so the escalation is in resolvability, not in
  the reported level. This is a durability escalation, never a loss,
  and it narrows the W3-1 "Buffered resolves Pending forever" limit to
  stores that never compact; the w3 record's limit text now carries this
  pointer.

## D7 — The verify path accepts compacted stores (a recovery-path addition)

WAL truncation removes the old manifest-corruption fallback (rebuild from
the verified WAL prefix). The open path gains the replacement the root
format already supported: **when the manifest is absent or does not
verify, recovery scans `roots/` and bases the rebuild on the
highest-sequence root file that verifies** (checksum, body decode, and
file-name identity all checked; anything else is skipped). D2's rule that
the manifest root is never deleted guarantees at least one verifying root
survives every pass. With the manifest healthy — the only state a
compacted store is normally opened in — recovery is unchanged: verify
manifest, load its root, roll forward WAL commits newer than it (none, or
post-pass commits).

This is a recovery **behavior** addition, not a format change: no
existing frame, record, or file layout changed. `FORMAT_VERSION` stays 1,
and a compacted and a never-compacted location open interchangeably.

One bookkeeping rule completes the fallback: a write-ahead commit at or
below the loaded base sequence still receives its `Published` ledger
record when recovery finds none (`record_publication` is idempotent), so
the commit resolves `Committed` and a same-identity retry replays — with
a verified manifest the guard no-ops, because the record already exists.
The attribution in that record is **at the loaded base**: the root id it
names is the base root the rebuild loaded, not the commit's own root
identity, which the fallback path never observed (the loaded table
already reflects the commit's writes, so no per-commit root is written
for it either). The resolution and replay semantics are unchanged; only
the recorded root identity is coarser than a normally published commit's.
The commit's *journal* record is deliberately not reconstructed on this
path: a correct write set cannot be derived without the pre-commit table,
and a fabricated one would poison retained-evidence extraction. The
disclosed consequence is fail-closed — such a commit is not
journal-servable, and the D5 divergence gate refuses history reclaim
below the journal's verified window for as long as its record is
missing. The other phases (checkpoint, WAL reclaim, root reclaim) still
run.

### Format changes named (per the storage-format rule)

- New file: `retained` (new artifact; D3 defines its records).
- `history` may be *shortened* to its retained records plus a leading
  high-water mark record — both existing record kinds; no new kind.
- `wal` may be empty; `roots/` may hold fewer files. No frame codec
  changed anywhere.

## Rejected alternatives

- **Delta/segment root format** (per-commit O(1) writes): the registered
  residual's write-amplification half. Rejected for this packet: the W3
  core contract mandates the per-commit publication order (private root,
  root file, manifest publish per commit), and a segment format reopens
  recovery, snapshot roots, and the manifest contract at once. Compaction
  bounds the *accumulation*; the *per-commit* cost remains the named
  deferred limit below, with no benchmark claim made here either way.
- **WAL rotation/segments** instead of truncation: more moving parts, a
  new naming scheme, and no correctness gain over checkpoint-then-
  truncate with the D7 fallback.
- **Rewriting or pruning the ledger**: identity resolution is unbounded
  in time by contract; a shed record would turn a legal retry into a
  duplicate publication.
- **In-memory history pruning below the bound**: rejected because a
  snapshot older than the floor is still readable by a live holder
  (transactions and query/stream reads borrow the Engine), and the
  current `table_at` fallback would silently serve such a holder the
  *published* table — a lost-isolation regression. Bounding the in-memory
  map needs a live-snapshot floor tracked across every snapshot holder;
  that is read-seam work, not compaction work (named below).
- **Automatic compaction triggers** (byte thresholds, commit counts): no
  policy surface exists; the host owns cadence (D1).

## Evidence

- Focused in-crate drills: `src/storage/compaction_tests.rs` — the
  kill-point matrix (D4), the divergence and unreadable-window gates
  (D5), the retention floor, the store's damage discipline (D3,
  including the corrupted-length-field shape), the
  corrupt-manifest-after-truncation recovery (D7), the Buffered-tail
  escalation and replay-identity preservation (D6), high-water and
  window survival across the rewrite, and kill-then-retry idempotency.
- The production declaration path with data: `src/engine/tests.rs` —
  `compact_with_retention_declares_and_rederives_the_bound_on_a_data_bearing_engine`
  drives `Engine::compact_with_retention` on an engine that committed
  real documents: the declaration (bound, held tombstone,
  `non_deletion_through`) on the first pass, and the durable prologue's
  restart re-derivation on the reopened engine;
  `compact_with_retention_lowers_the_watermarks_floor_into_the_pass`
  proves the handle's retention floor is lowered into the pass. The
  watermark form the
  prologue uses, `declare_history_bound_retained`, has direct rows in
  `src/replication/retention_tests.rs` (its three refusal gates and the
  non-deletion max-merge).
- Public-seam tests: `tests/recovery/compaction_public.rs` — the typed
  request and bounded progress/fact reporting, the fail-closed
  unservable-window refusal and the fail-closed empty-handle shape of
  `Engine::compact_with_retention`, and composition with the public
  backup/restore seam. The data-bearing shapes live in the in-crate
  tests above (`TenantScope` has no public constructor by design).
- Recovery Receipt (exclusive lane): `evidence/recovery/
  gaps1-compaction-recovery-receipt.md`, including its per-drill-class
  mutation-kill record.

No performance claim is made anywhere in this packet: compaction facts
are counts and byte lengths observed by tests, never speed statements
(PD-2026-08-26-03; any such claim needs the benchmark lane).

## Limits

- **The per-commit write path is unchanged**: every commit still applies
  its write set to a full-table private root and writes a full root file.
  Compaction reclaims accumulated artifacts; it does not make commits
  O(1). Deferred with the storage seam as owner; any future change
  requires its own decision record and the exclusive recovery lane.
- **The in-memory root-history map is not pruned** (the rejected
  alternative above): it grows with commits for the process's lifetime.
  The durable root-file growth — the registered residual — is reclaimed.
- **The operation ledger grows without bound**, by decision (D6).
- **No automatic trigger**: the capability runs only when a caller asks.
- **One pass is one whole window**: extraction and the journal rewrite
  materialize the retained records in memory (bounded by the window being
  given up, which the pass itself chooses); there is no incremental
  batching across calls yet.
- **The retained store grows with retained facts**: below-floor facts are
  skipped only when a watermark handle names the floor; there is no
  store-side compaction of facts whose floor has long passed (a future
  pass could rewrite it under the same D4 discipline).
- **Stamps are not durable** (D3): re-derived evidence carries the origin
  stamp.
- Single-Engine-per-location, no lock file, no power-loss simulation:
  unchanged W3-1 assumptions; durability is observed through process exit
  and reopen only.
