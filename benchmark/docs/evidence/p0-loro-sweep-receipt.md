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
