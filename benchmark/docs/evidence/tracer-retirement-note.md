# Tracer lane retirement note

Status: recorded 2026-08-29 at develop `bafabd2` (parent `59ce520`) by
the engine-tracer-retire lane. This is a verify-then-delete receipt for
the `tracer/` directory. Nothing here is a performance or product
claim; the packet's only claim is that the directory was dead when it
was deleted.

## What was deleted

Commit `bafabd2` deleted the whole `tracer/` directory — the historical
SE-11 end-to-end client live-update tracer harness:

- `tracer/README.md` (self-described "Historical Loro-era tracer" banner,
  added by `664ec1c`);
- `tracer/dotnet/TerraScale.Tracer/Program.cs`;
- `tracer/dotnet/TerraScale.Tracer/TerraScale.Tracer.csproj`;
- `tracer/ts/tracer.mjs`;
- `tracer/.gitignore`.

That is all 5 git-tracked files under `tracer/`
(`git ls-files tracer/` before deletion). The directory's untracked
local build outputs (`tracer/dotnet/TerraScale.Tracer/bin/**` and
`obj/**`, roughly 12 MB) were removed from disk with `rm -rf tracer`
after the `git rm`.

## Why it was dead (verification record)

Every check below ran on 2026-08-29 against `59ce520` (the commit the
deletion sits on). Working directory: the TerraBaseV2 checkout root.

1. CI: `grep -n -i -E "se11|tracer" .forgejo/workflows/ci.yml` — no
   matches. Full read of `ci.yml` confirms the gates build only the
   cargo workspace (`cargo * --workspace --locked`, which cannot reach
   `tracer/` — it is not a member), the explicit C# projects under
   `host/` and `cli/`, and `studio/`. No gate builds, tests, or
   references the tracer.
2. Cargo workspace: the root `Cargo.toml` `[workspace] members` list is
   `crates/*` only; `tracer/` contains no Rust and no crate path
   dependency references it.
3. Solutions and project graph: the repository's only solution file is
   `cli/TerraBase.Cli.slnx` (contains `TerraBase.Cli` and its tests).
   A repo-wide search for `TerraScale.Tracer` across
   `*.csproj|*.sln|*.slnx|*.props|*.targets` in TerraBaseV2 and the
   sibling TerraScale.DotNet checkout matched only the tracer's own
   csproj. No project references it.
4. `scripts/check.sh`: `grep -n -i -E "se11|tracer" scripts/check.sh` —
   no matches. (The script was read/grepped only; the workspace rule
   forbids running it.)
5. Development documentation: no README or `docs/` development
   instruction outside `docs/evidence/**` references the tracer. The
   only instruction-like text mentioning `scripts/se11/run.py` was
   `tracer/README.md` itself (deleted) and the immutable receipts.
6. Path-reference census: `grep -rn -E "tracer/|TerraScale\.Tracer"`
   across TerraBaseV2, TerraScale.DotNet, TerraScale.TypeScript, and
   TerraScale.Database (excluding `tracer/` itself, `bin/`, `obj/`,
   `target/`, `node_modules/`, `dist/`, and the se11 receipts
   directory) found exactly two files:
   - `docs/evidence/p0-loro-sweep-receipt.md` — an evidence receipt;
     per the packet rules historical receipts may cite the tracer and
     are not live references. That receipt classifies `tracer/` as
     historical-only (category 6: "a self-described 'Historical
     Loro-era tracer' that does not qualify the replacement plane").
   - `scripts/se11/run.py` — see below.
7. Word-only matches: the remaining `tracer` hits in `src/**`, `tests/**`,
   and `host/**` (e.g. "tracer-scale limit", "W3-2 tracer rule",
   "in-process tracer tests", the `host/TerraBase.Api.Tests/
   SyncSessionTests.cs` comment recalling "the SE-11 tracer") are prose
   uses of the word or history comments, not references to the
   directory. None import, build, or invoke it.

## Compile proof (the directory was not merely unused but non-viable)

`tracer/dotnet/TerraScale.Tracer/Program.cs` still mapped the dead Loro
payload codec names:

```csharp
TerraScaleSyncV2PayloadCodec.LoroDelta1 => "loro-delta/1",
TerraScaleSyncV2PayloadCodec.LoroSnapshot1 => "loro-snapshot/1",
```

The current TerraScale.DotNet SDK no longer defines those enum members —
`TerraScaleSyncV2Protocol.cs` carries only `NativeDelta1 = 3`
(`terrabase-native/1`) and `NativeSnapshot1 = 4`
(`terrabase-snapshot/1`) — and `crates/terrabase-sync-core/src` has zero
`loro` matches. A focused build was run as the final proof:

```
cd tracer/dotnet/TerraScale.Tracer
TERRASCALE_DOTNET_SDK=<sibling TerraScale.DotNet csproj> dotnet build -c Debug

Program.cs(477,38): error CS0117: 'TerraScaleSyncV2PayloadCodec'
  does not contain a definition for 'LoroDelta1'
Program.cs(478,38): error CS0117: 'TerraScaleSyncV2PayloadCodec'
  does not contain a definition for 'LoroSnapshot1'
Build FAILED. 2 Error(s).
```

So the SE-11 orchestrator's .NET tracer leg has been unbuildable since
the Loro sweep removed the codecs; the directory cannot be resurrected
by anyone running the old harness against the current SDK.

## Remaining references after deletion

- `scripts/se11/run.py` (outside this lane's owned paths, left
  untouched): the retired SE-11 orchestrator. It is invoked by nothing
  (CI, check.sh, scripts, and docs instructions were all checked), its
  evidence corpus `docs/evidence/sync/se11/*` is immutable history, the
  closed P0 Loro-sweep receipt names "the SE-11 script and spike
  evidence" among the historical-only remaining mentions, and it cannot
  complete a run because its .NET tracer leg no longer compiles (proof
  above). It now references deleted paths; it should be retired by its
  owning lane the same way. Until then it is dead code pointing at a
  deleted directory, which is strictly more honest than a tracer that
  looks runnable but cannot build.
- `docs/evidence/p0-loro-sweep-receipt.md` and the
  `docs/evidence/sync/se11/` receipts: immutable evidence records;
  intentionally unchanged.

## Honest limits

- The census is point-in-time at `59ce520` with concurrent lanes live;
  another lane could in principle reintroduce a tracer reference after
  this commit (none existed at deletion time).
- The TypeScript tracer leg was not executed end to end; it treats
  payload codecs opaquely and may still load against the sibling
  `TerraScale.TypeScript` dist. This packet did not need that result:
  the whole directory is one historical unit, the .NET half is
  provably non-viable, and no build graph or gate reaches either half.
- No SSD-sensitive work was performed: one Debug restore/build attempt
  (~10 s, artifacts since deleted with the directory) and greps.
