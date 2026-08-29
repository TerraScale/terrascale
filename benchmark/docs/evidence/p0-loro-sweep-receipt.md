# P0 Loro-sweep receipt

Status: recorded 2026-08-29 at develop `661ce9a` by the P0 sweep
evidence lane. Records what the sweep's prior steps changed, the
verified Loro state of `crates/**`, and the categories of remaining
historical-only mentions. This lane's SSD line is appended to
`p7-ssd-budget-usage-record.md`.

## Active files fixed or removed by the sweep's prior steps

- `b7097a4` deleted the SE-02 sync spike `spike/sync-core/` (11 files,
  3273 deletions), the deletion ADR 0022 claims. At its parent the
  spike carried 106 Loro references across 9 files
  (`git grep -i loro b7097a4^ -- spike/`); none remain anywhere. The
  root workspace still resolves (9 members, `cargo metadata
  --no-deps`), and CI, the root `Cargo.toml`, and the root
  `Cargo.lock` carry no spike references.
- `661ce9a` marked the C# host lane as a migration source and its
  Loro codecs legacy: `host/README.md` (+12 lines) plus one legacy
  codec comment in each of `host/TerraBase.Api.Tests/
  RealEngineTests.cs`, `ScriptedTbepEngine.cs` (2),
  `SyncGoldenVectorTests.cs`, and `SyncSessionTests.cs` — verified
  comment-only in the diff; the Api test project built 0 warnings /
  0 errors in Release during that step.
- `docs/design/**` was nothing-to-do: the only two Loro-mentioning
  design contracts (`p0-leaderless-replication-contract.md`,
  `p0-schema-merge-contract.md`) already carry supersession banners
  (commit `664ec1c`) citing ADR 0019 and the workspace charter with
  do-not-extend language; the other four P0 design contracts carry
  zero Loro mentions.

## Verified Loro state of crates/** — NOT yet clean

Check run at `661ce9a`:
`grep -ri loro --include='*.rs' --include='*.toml' crates/ | grep -v target`
— the sweep expects this to return nothing. Actual result: **130
matching lines across 17 files in 4 crates**, and `loro` remains
pinned in the root `Cargo.lock` (`name = "loro"`):

- `crates/terrabase-sync-core/` — live `loro = "1.13.9"` dependency;
  `src/lib.rs` imports Loro types; `src/protocol/body.rs` defines the
  `LoroDelta1`/`LoroSnapshot1` payload codecs with the
  `loro-delta/1`/`loro-snapshot/1` wire names; five test files.
- `crates/terrabase-replication-core/` — live `loro = "1.13.9"`
  dependency; `src/lib.rs` and `src/compose.rs` implement the Loro
  CRDT plane; `tests/leaderless_convergence.rs`.
- `crates/terrabase-tbep/` — `src/host.rs` emits the Loro codec
  strings; `src/generated/registry.rs` registers them; two test files.
- `crates/terrabase-sync-conformance/` — `src/corpus.rs` maps the
  codec names.

So the charter invariant "no Loro references in `crates/**`" is NOT
met at this receipt's HEAD; the sweep's code-layer de-Loro work
remains open for the crates-owning lanes. This evidence lane owns
`docs/evidence/**` only, changed no code, and did not touch the
`terrabase-crdt`/`terrabase-causal` crates owned by another lane
(neither appears in the grep results).

## Remaining Loro mentions — historical-only categories

Mentions outside `crates/` are historical, each with supersession or
migration context (categories, not an exhaustive file list):

1. ADRs (9 files) — the supersession authorities 0019, 0022, 0024 and
   the historical decisions 0003, 0005, 0008, 0013, 0014, 0018.
2. Superseded design contracts (2 files) carrying the banners above.
3. The workspace charter `../GOAL.md` (21 mentions) — defines Loro as
   an external benchmark reference only.
4. Repo `README.md` — the honest current-state list ("Loro-backed
   sync and replication crates") and the required-replacement-work
   section ordering Loro out of persistence, replication, sync, and
   supported wire formats.
5. The `host/` C# migration lane — README note, the legacy codec
   comments from `661ce9a`, and the generated
   `host/TerraBase.Protocol/Generated/TbepRegistry.g.cs` codec
   strings, all retiring with that lane's cutover.
6. `tracer/` — a self-described "Historical Loro-era tracer" that
   does not qualify the replacement plane.
7. Immutable past-run receipts (for example
   `docs/evidence/sync/se11/s1-golden-path.md`).

## Honest limits

- Counts are point-in-time at `661ce9a` on 2026-08-29 with concurrent
  lanes live; the crates numbers may drop as other lanes land de-Loro
  work after this receipt.
- The build/test receipts quoted for the prior steps come from those
  steps' lanes; this lane ran only the greps, `du`, and git checks
  recorded here.
- `src/index/mod.rs` and `src/index/tests.rs` were dirty from another
  lane throughout and were not touched.

## Close-out 2026-08-29 — the sweep is complete at the code layer

Appended 2026-08-29 by the host-lane close-out packet at develop
`7dde68e` (this section supersedes the "NOT yet clean" section above;
the historical counts there remain accurate for `661ce9a`). What
landed after `661ce9a`:

- Engine crates, by the concurrent crates lanes: the replication core
  rewritten onto `terrabase-crdt`/`terrabase-causal` (`8d99b00`), the
  cluster enumeration seam (`042332b`), the sync-core native
  result-view state machine plus corpus migration (`1ea5be2`,
  `d67c4c4`), and the TBEP delivery binding with the generated
  registry regenerated to the native codec vocabulary (`7dde68e`).
- Host lane, by this packet (carried onto develop by the sibling
  integrator lane as `cddadfd`, stat-identical to this packet's
  working tree; this receipt file is this packet's own commit):
  `ScriptedTbepEngine.cs` now scripts
  `terrabase-native/1` and `terrabase-snapshot/1`; the snapshot
  assertions in `RealEngineTests.cs`, `SyncSessionTests.cs`, and
  `SyncGoldenVectorTests.cs` and the `RealEngineTests` header comment
  follow; `host/README.md` states the lane mirrors the native codecs
  (registry regenerated 2026-08-29); the root `README.md`
  honest-status list now reads "native CRDT and causal sync and
  replication crates" and the required-replacement bullet records the
  completed removal with its remaining follow-through.

### Verified Loro state at this close-out — clean

Run against the working tree at `7dde68e` plus this packet's edits
(after the greps, the counts were re-verified unchanged):

- `grep -ri loro --include='*.rs' --include='*.toml' crates/ |
  grep -v target` — **0 lines** (130 across 17 files at `661ce9a`).
- Broader `grep -ri loro crates/` over every file type, excluding
  `target/` — **0 lines**.
- `grep -ri loro --include='*.rs' src/` — **0 lines**.
- `grep 'name = "loro"' Cargo.lock` — **0**; the pin is gone, so no
  root `Cargo.lock` edit was needed by this packet.
- `grep -rn -i loro host/ --include='*.cs'` — **0 lines** (generated
  `TbepRegistry.g.cs` already carried only the native vocabulary).

### Checks run by this packet

- `dotnet run --project host/TerraBase.Api.Tests/
  TerraBase.Api.Tests.csproj --configuration Release` — 93 rows,
  92 passed, 0 failed, 1 skipped (the pre-existing msquic/QUIC
  platform skip, unrelated to the sweep). The real-engine rows pass
  asserting `terrabase-snapshot/1` end to end, so the old
  `loro-snapshot/1` expectations were dead against the migrated
  engine, not merely stale prose. Re-run at develop `f5a18ee`
  (after two sibling engine commits landed mid-wave): same result,
  93 rows, 92 passed, 1 skipped.
- The greps above. No Rust crate was touched by this packet, and no
  `.forgejo/workflows/ci.yml` change was needed (test-content only).

### Honestly remaining

1. `tests/architecture/forbidden_imports.rs` still scans
   `LORO_FREE_ROOTS = ["src", "crates/terrabase-causal/src",
   "crates/terrabase-crdt/src"]`; with the sweep landed, the
   gate-owning lane can add `crates/terrabase-sync-core/src` and
   `crates/terrabase-replication-core/src` — its own review note says
   they join "when those land". The same lane can un-dorm the
   pre-registered edges in `dependency_direction.rs` (its note at
   lines 48-55). Not edited here: those files are outside this
   packet's named paths.
2. `TerraScale.Database` must re-vendor the regenerated
   `TbepRegistry.g.cs` — resolved during this close-out window:
   that repository's `1c17026` ("Regenerate native TBEP codec
   registry") re-vendored it, and its `PayloadCodecs` vocabulary was
   verified identical to the host's regenerated registry (both carry
   only the five native identifiers, no `loro-*`). That lane also
   amended the root `README.md` bullet to state the re-vendor is
   landed; the amendment is truthful and rode to develop inside
   `cddadfd` with this packet's host edits.
3. Historical-only mentions, each intentional: the ADRs (the
   supersession authorities and historical decisions), the superseded
   design contracts, `../GOAL.md` (the charter defines Loro as an
   external benchmark reference only), `AGENTS.md` (human-owned, not
   agent-editable), the `tracer/` historical record, the SE-11 script
   and spike evidence, and immutable past receipts — including this
   file's own historical sections above.
