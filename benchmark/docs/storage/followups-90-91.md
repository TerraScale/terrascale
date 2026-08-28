# Rust follow-ups record (#90 warden re-gate MINORs + #91 item 1)

Status: **built for this packet** — every item below is implemented by this
packet's code and proved by its tests and receipts. The PROPOSED decisions
are labeled for parent ratification.

Owner: fups-rust (ticket #90: GAP-S1 write-side retained-record ceiling,
SE-13 envelope command labeling, rustdoc links; ticket #91 item 1:
AddOrPatch create-leg identity check). Base commit `b94d49e`.

Coordinates: GAP-S1 (#81, `docs/storage/gaps1-storage-compaction.md` — D3
owns the damage discipline this record's first fix enforces; D4 owns the
kill-point model the refuse-versus-chunk decision reasons over),
GAP-R02 (#79, `docs/replication/tombstone-retention-watermark.md` — the
non-deletion semantics the decision protects), SE-13 (#12, the operator
CLI), GAP-D07 (#60, `docs/engine/gapd07-patch-language.md` — the patch
vocabulary the #91 fix extends).

## What landed

1. **#90 (1) — the write-side retained-record ceiling**
   (`src/storage/retained.rs`). `RetainedWriter::append_bound` now refuses,
   before one byte is written, any bound record whose payload would exceed
   `MAX_RETAINED_RECORD_BYTES` (1 MiB): the reader classifies a declared
   length past that ceiling as a damaged length field **wherever it sits**
   (`RetainedIndex::scan`'s `length > MAX_RETAINED_RECORD_BYTES` arm), so
   the pre-fix writer could append an intact, legal record that its own
   interrupted append — a prefix of that frame — would misclassify as
   integrity damage, failing the open with `FormatMismatch` exactly where
   the documented rule promises torn-tail truncation. Four drills landed
   inline in the module (red-then-green for the refusal): the largest
   legal window (21 fixed bytes + one 268-byte maximum-width row per
   tenant, exactly under the ceiling) writes and round-trips; one
   maximum-width row past it is refused with
   `io::ErrorKind::InvalidInput` and zero bytes written; a **trailing**
   frame declaring an impossible length is integrity damage, never a torn
   tail; a trailing frame declaring a legal length with its payload cut
   off keeps the gentler torn-tail reading. The module-root and constant
   doc comments now state the write-side enforcement and its scope.
2. **#90 (2) — SE-13 usage envelopes name the full command path**
   (`cli/TerraBase.Cli`). `Cli.ExtractCommandName` joined two-word command
   paths only when the first plain token was `query` or `engine`, so every
   document refusal (`document read/write/delete/browse`) labeled the
   machine usage envelope's `command` field with the bare word
   `"document"`. The label now derives from the grammar's own table — the
   new `CliGrammar.CommandLabel`, the same longest-prefix walk
   `ParseCommand` uses — and is pinned in the grammar rows with one row
   per document subcommand plus regression pins for `query cancel`,
   `engine shutdown`, and single-word commands.
3. **#90 (3) — rustdoc warnings-clean.** Baseline `cargo doc` at this
   base carried seven warnings. Two were in this packet's scope and are
   fixed in the main history: the public
   `TombstoneRetention::declare_history_bound` doc linking the
   crate-private `declare_history_bound_from_commits`
   (`src/replication/retention.rs`, now a plain code span — the sentence
   already names it crate-private), and the redundant explicit
   `TbepHost` link target in `crates/terrabase-tbep/src/server.rs`. The
   other five sit outside this packet's write scope and are a single
   PROPOSED collision commit (below). `cargo doc --workspace --no-deps
   --locked` now emits zero warnings.
4. **#91 item 1 — AddOrPatch create-leg identity check**
   (`src/engine/patch.rs`). `apply_add_or_patch` staged
   `add_or_patch.if_missing()` verbatim under `ExpectedVersion::New`, so
   a compound whose create document carried a different id or collection
   than the addressed pair silently wrote to the unaddressed target. The
   seam now refuses the compound — before reading or staging — with
   `PatchError::Construction(RequestValidationError::InvalidPatch)`,
   pinned red-then-green with both the id and collection rows and
   post-conditions that nothing staged and neither target exists.

## Decision records

### D1 — Enforce the ceiling by refusing, not chunking (PROPOSED)

The ticket allowed either shape. **Decision: refuse in `append_bound`.**

- *Chunking* (splitting the non-deletion rows across multiple bound
  records — read-compatible, because `scan` already merges `bound` by max
  and `non_deletion` per tenant by max across every bound record,
  `src/storage/retained.rs` merge in the scan loop) is **rejected for
  this packet on crash-safety grounds**: the store's durability model is
  process kill (the GAP-S1 record's Limits: "durability is observed
  through process exit and reopen only"), and bytes written but not yet
  `sync_data`'d still reach a reopening reader through the page cache. A
  kill between two complete chunk records leaves a **complete** bound
  record with a **partial** non-deletion set durable, and the D4
  kill-point table's "no bound is durable" row no longer holds. The
  consumer consequence is the delayed-data-loss class GAP-R02 exists to
  prevent: a tenant whose row did not land reads as
  `non_deletion_through == None`, and both gates that consume it — the
  member catch-up gap refusal (`src/replication/member.rs`, the
  `non_deletion_through ... > cursor` arm) and backup provability
  (`src/backup/mod.rs`, the `non_deletion_through(tenant) <= base`
  condition) — would pass a consumer that missed a put below the bound,
  silently. A safe chunking needs the declaration ordering redesigned
  (for example a terminal declaration record) plus prologue and gate
  changes in files outside this packet's scope (`compaction.rs`,
  `file.rs`); that is named below as the storage-seam follow-up.
- *Refusing* is fail-closed and idempotent: the refusal happens before
  any byte is written, the journal rewrite that follows evidence
  extraction never runs, the window is intact, and facts already
  appended by the same pass dedup on the retry (D6 of the GAP-S1
  record).
- **Disclosed consequence (the limit that makes this PROPOSED):** a
  `ReclaimHistory` pass whose bound record would pass the ceiling fails
  the whole pass. The row is `13 + len(tenant)` bytes (length prefix,
  flag, sequence), so the ceiling admits 3,912 maximum-width 255-byte
  tenants — far above any window this engine has been driven with in
  tests — but there is no chunking behind it. The refusal surfaces as
  `CompactionError::Storage` because `compaction.rs` (read-only for this
  packet) maps every store error there; threading a typed, disclosed
  variant is named below.

### D2 — The refusal is a typed input error with a bounded message

`io::ErrorKind::InvalidInput` carrying payload bytes, row count, and the
ceiling — counts and byte lengths only, no identifiers. The message does
not currently cross a public seam (the caller maps it away); it exists
for the follow-up that threads it.

### D3 — The SE-13 label comes from the grammar table, not a root list

Adding `"document"` to `ExtractCommandName`'s `is "query" or "engine"`
set would fix today's miss and re-break on the next multi-word command.
`CliGrammar.CommandLabel` reuses the table and the longest-prefix walk
`ParseCommand` already documents, so the label and the grammar cannot
drift. Residual (pre-existing, disclosed, unchanged): the fallback
plain-token walk in `ExtractCommandName` is best-effort for shapes the
grammar refuses mid-tokenize — an unknown flag's would-be value is
skipped, so `terrabase --nope status` labels `""` (observed while
writing the red rows; the single-word pin row uses a grammar-labeled
shape instead). Fixing it means duplicating the flag table into the
non-throwing walk; left to the SE-13 owner as optional polish.

### D4 — The #91 refusal reuses the Construction variant

`PatchError::Construction(RequestValidationError::InvalidPatch)` is the
variant's documented role — "the shape checks the seam re-checks at
application time" — and `InvalidPatch` is the closed malformed-patch
vocabulary. A dedicated variant was rejected: it grows the public error
surface for a case the existing vocabulary names exactly.

### D5 — Doc-link fixes: backtick or drop the target, never widen scope

The private-item link became a plain code span (the surrounding sentence
already says the item is crate-private); each redundant explicit target
dropped its destination per rustdoc's own suggestion; the unresolved
`Cancellation` link gained its resolving path. Note for the warden: the
ticket named "two in terrabase-tbep", but the baseline at `b94d49e` has
exactly one tbep warning (`server.rs:8`); the full baseline is seven
warnings across six files (one private-item, one unresolved, five
redundant-target). All seven are gone; five needed the collision commit.

## V1 citations (pin `fa99b9257776dbe625e6a2388c74c9ce916637ee`)

- **#91 (the mirrored behavior):** V1's create-if-missing path never
  takes an identity from the create value — `DocumentsStorage.Put(context, id, ...)`
  stores under the **command's addressed key**
  (`src/TerraBase.Server/Documents/Patch/PatchDocumentCommand.cs:186`),
  with the create body returned verbatim as the document
  (`PatchDocumentCommand.cs:284-289`, `ExecuteScript` returning
  `_createIfMissing` when no document instance exists). The V2 refusal
  preserves exactly that "addressed key wins" semantics at the typed
  seam, where the `Document` happens to carry its own identity.
- **#90 (no V1 counterpart, stated):** the retained-evidence store is a
  V2 artifact born of GAP-R02/GAP-S1; its damage discipline is the
  GAP-S1 record's D3 (`docs/storage/gaps1-storage-compaction.md`,
  "Damage discipline" paragraph, including the corrupted-length-field
  rule this packet makes writer-enforced). No V1 file mirrors it and no
  parity row changed.
- **SE-13:** V1 has no operator CLI to mirror; the envelope contract is
  the landed SE-13 packet's own (`cli/README.md`, machine envelope
  version 1, unchanged by this packet — only the `command` label's
  precision changed).

## Receipts

Pinned toolchain (ambient `RUSTUP_TOOLCHAIN` unset for every cargo
invocation; `scripts/check.sh` re-unsets it itself):

| Command | Exit |
| --- | --- |
| `cargo fmt --all -- --check` | 0 |
| `cargo check --workspace --locked` | 0 |
| `cargo test --workspace --locked` (dev profile) | 0 — every suite green, including the 41 storage compaction drills, 813 lib tests, and the workspace integration suites |
| `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings` | 0 (second run; first run 101 — two test-code lints in the new drills, fixed in `8bbdda6`, both runs disclosed) |
| `cargo doc --workspace --no-deps --locked` | 0, zero warnings (baseline: 7) |
| `TERRABASE_V1_ROOT=.../terrabase-v1-fa99b925-readonly ./scripts/check.sh` | 0 — "TerraBaseV2 foundation checks passed" (second run; the first run exited 1 at the final V1-citation step only because the doubly-nested worktree needs `TERRASCALE_WORKSPACE_ROOT=/home/mariogk/Projects/TerraScale` for the audit to find `specs/` — an environment resolution issue, every code step before it already green; both runs disclosed). Inside the passing run: fmt, check, dev tests, release tests (90 `test result: ok` suites across both profiles, zero failures), clippy `-D warnings`, doc, parity tooling, secret scan, license, benchmark-receipt discipline, secrets scan, and the strict V1 citation audit. `cargo-deny` is not installed on this host and is recorded by the script itself as unrun. |
| `dotnet run --project TerraBase.Cli.Tests` (SDK 10.0.400 per `global.json`) | 0 — 41/41, 2m20s |

Red-then-green evidence:

- **Retained ceiling:** red run —
  `append_bound_refuses_a_window_past_the_record_ceiling` panicked on
  `expect_err` receiving `()` (the writer accepted the 3,913-row window);
  green after the fix, 4/4 module drills and the 41 storage drills pass.
- **AddOrPatch:** red run — the retarget test failed on `Created` being
  returned; green after the fix, 23/23 patch tests, 95/95 engine lib
  tests.
- **SE-13:** red run — the four document rows failed with exactly
  `Expected to be equal to "document write" but received "document"`
  (and the initial single-word row exposed the pre-existing `""`
  best-effort label described in D3); green after the fix, 25/25 grammar
  rows, 41/41 full CLI family.

## Deferred items (with owners)

- **Chunked bound records** with a crash-safe declaration ordering (the
  D1 analysis), so windows past the ceiling can still be given up —
  **storage seam, GAP-S1 successor**; needs `compaction.rs`/`file.rs`
  changes and the exclusive recovery lane.
- **A typed compaction refusal carrying the ceiling diagnostic** across
  the seam (today `CompactionError::Storage`) and letting the
  checkpoint/WAL/root phases still run — **storage seam**; `compaction.rs`
  is read-only for this packet.
- **#91 item 2** (the array-index `Replace` coverage row) and **#91
  item 3** (the three W4-STREAM boundary matrix rows) — **the separate
  #91 remainder packet the dispatcher holds**; verified untouched here
  (the existing patch coverage has `Add` at an out-of-bounds index and
  `Replace` at an absent field; no array-index `Replace` row was added,
  no streaming file was touched).
- **The SE-13 best-effort label residual** (`--nope status` labels
  `""`) — **SE-13 owner**, optional polish per D3.
- **The "second tbep warning" discrepancy** vs the warden's count —
  **warden/parent**; one existed at this base.

## Proposed collision actions

- **TAKEN, PROPOSED for ratification** — commit `e85a88b`
  "collision: doc-comment-only link fixes in five out-of-packet-scope
  files": `src/engine/ingest.rs` (the `Cancellation` link now resolves to
  `crate::types::Cancellation`), `src/engine/mod.rs` (`StreamStore`),
  `src/families/hilo/mod.rs` (`IDENTITY_CEILING`),
  `src/query/includes.rs` (`QueryError::IncludeCycle`),
  `src/query/mod.rs` (`MAX_INCLUDE_DOCUMENTS`). Every edit is a
  doc-comment-only link fix; dropping the commit restores the baseline
  warnings. Required because #90 (3) asks for a warnings-clean
  `cargo doc`, which the two in-scope fixes alone cannot deliver.
- **NOT taken:** threading a typed compaction-error variant through
  `compaction.rs` for the ceiling refusal (deferred above rather than
  collided — it is a behavior decision, not a mechanical fix).

## Disclosed limits

- A window whose bound record would pass the ceiling fails the whole
  `ReclaimHistory` pass (D1) — no chunking exists yet.
- The ceiling refusal's diagnostic does not cross the compaction seam
  (D2).
- Fact records carry no writer-side ceiling check: they are structurally
  bounded at roughly one KiB (four ≤255-byte identifiers plus fixed
  fields) and cannot approach 1 MiB; documented in the constant's doc
  comment, not enforced redundantly in code.
- The read-side damage classification is **unchanged** — the two
  read-side drills pin existing behavior so the writer fix cannot relax
  it; they were green before and after.
- The usage-envelope label fix touches only usage envelopes; result,
  notice, event, typed-refusal, and unavailable envelopes already named
  the full path via `command.DisplayName` and are unchanged.

## Non-claims

- No performance claim anywhere in this packet: every retained-store
  number is a count or a byte length observed by tests.
- No claim that large-window history reclaim now works — it refuses.
- No parity row was flipped, and no `parity/**` file was touched.
- No format change: no record kind, frame layout, or file naming
  changed; the writer merely refuses a shape it should never have
  written.
- No claim the CLI label fix changes machine-envelope version 1 — the
  envelope shape is untouched; only the `command` value's precision.
