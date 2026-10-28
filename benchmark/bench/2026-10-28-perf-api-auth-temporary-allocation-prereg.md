# Auth temporary allocations: preregistered paired measurement (2026-10-28)

**Status: PREREGISTERED, NOT LAUNCHED. No timing has run and this file claims no timing result.** Before the single launch
below, the parent must approve this committed preregistration and the separate independent source review of the candidate
must be complete. After launch nothing in the protocol or rules changes, and there are no reruns.

## Candidate and mechanism

Candidate `fix/terrabase-auth-temporary-allocations` @ `4cba57edbe8b3bf93944455c15203678788daf15`, against main
`9cbc00d60788ab383917cf8768879beffec4fc0c` (its parent; the candidate is two commits on it). The production delta is two files:

- `ValidatedTokens.KeyOf`: the cache key is still Base64(SHA-256(UTF-8(token))). The SHA-256 digest is now written to the
  stack. The token's UTF-8 bytes go into a fixed 1,024-byte stack buffer through `Encoding.UTF8.TryGetBytes`. A token that
  does not fit falls back to the old allocating `Encoding.UTF8.GetBytes`. A null token throws `ArgumentNullException`, as
  before.
- `UserAuthenticationHandler.Success`: the code creates the `ClaimsIdentity` first. Each `Claim` is then created with that
  identity as its subject, with string value type and the default issuer as issuer and original issuer, so `ClaimsIdentity`
  keeps the claim instead of cloning it. The `Select` iterator is gone.

The candidate's third file, `tests/TerraBase.Fast/Management/UserAuthTests.cs`, is a Fast test. It is not built into either
arm.

This is not a new mechanism. Both parts were already measured today and rejected:

- [Token-hashing allocation experiment](2026-10-28-perf-api-token-hash.md)
  ([patch](2026-10-28-perf-api-token-hash-candidate.patch)) was rejected for mixed latency. That candidate stack-allocated up to
  2,048 bytes, used a cleared pooled buffer above that, and zeroed its scratch. This candidate uses a fixed 1,024-byte stack
  buffer and an allocating fallback, and does not clear the stack copy of the token's bytes. The independent review should weigh
  that difference.
- [Per-request claim materialization](2026-10-28-perf-api-claims.md)
  ([patch](2026-10-28-perf-api-claims-candidate.patch)) was rejected for a medium c=1 p50 and CPU regression. This candidate
  uses the same subject-identity form, with `foreach` over the tuples instead of an index loop.

This run does not reopen either decision. It measures this exact combined commit once, under the common-harness protocol of
[run common-r2](2026-10-28-perf-api-cached-system-open-common-harness.md). Whatever the outcome, those two reports stand.

## Inputs and source checks (asserted by the script before any build)

| Item | Value |
| --- | --- |
| Run script | `benchmark/scripts/api-auth-temporary-allocation-20261028.sh`. It derives from `api-cached-system-open-20261028.sh` at c67903c38 (build) and 7b3980c65 (common harness). |
| Summary | `benchmark/scripts/api-auth-temporary-allocation-summary.py`. Its gate, verdict, worsening and slow-state code is unchanged from `api-cached-system-open-summary.py` at 7b3980c65. It adds arm hashes, sample counts and the control output. |
| Observer | `benchmark/scripts/api-cpu-observer.py`, byte-identical to 7b3980c65 |
| Control | `benchmark/scripts/api-auth-token-key-control.cs` (see below) |
| SDK | ts-dotnet `36d114877a69d18fba33b27ad362bd3e68e9e7ff`, the established benchmark pin. CI's `TS_DOTNET_REV` 03881aa74 is its ancestor. E2E, TestHarness and `benchmark/dotnet` have not changed since a42f65d55, which built against this pin. |
| Native | `scripts/vendor/download-turso-native.sh` at baseline checks `SHA256SUMS` before and after the download. Hashes are recorded in `turso-native.sha256`. |
| Host / CPUs | delta, `taskset -c 2-11`, .NET from `global.json` (11.0.100-rc.1.26425.128). Runtimeconfig is set to workstation concurrent GC with TieredPGO, identical in both arms. |

Before any build, the script asserts:

- This branch adds only `benchmark/` files to main.
- Baseline is the merge base of the candidate.
- `git diff baseline candidate` names exactly the two UserAuth files and the Fast test.
- Over every build input (`src`, `Directory.*`, `global.json`, `NuGet.config`, `benchmark/dotnet`, `tests/TerraBase.E2E`,
  `tests/TerraBase.TestHarness`), only the two UserAuth files differ.
- `git grep` finds neither `ValidatedTokens` nor `UserAuthenticationHandler` in E2E, TestHarness or `benchmark/dotnet`.

## Common harness API compatibility (source-verified; smoke planned)

The baseline and the candidate have identical E2E, TestHarness and `benchmark/dotnet` sources. The server delta changes:

- the body of `KeyOf` (same `public static string KeyOf(string)` signature, on an internal type);
- one new private const, `StackBytes`;
- the body of a private method, `Success`.

The harness names neither type. A candidate-compiled `TerraBase.E2E.dll` therefore references no member the baseline server
lacks. This is a source argument, not metadata inspection.

The script backs the argument with the existing real-HTTP scenario
`RepairAndContractE2ETests/Every_region_publishes_the_replication_lag_series*`. It must pass 3/3 on each arm, with no retries,
before any timed arm runs.

## Protocol

1. Fetch both commits exactly. Create one detached worktree at a fixed path outside the job checkout. Build the baseline
   `tests/TerraBase.E2E` (Release, no build servers), check out the candidate in the same worktree and build it again. Every
   assembly except the server compiles from identical paths. Each build's own E2E DLL/PDB is kept and hashed
   (`own-harness.sha256`) but is not run.
2. Common harness: copy the candidate-compiled `TerraBase.E2E.dll`/`.pdb` into both arms. Freeze `before.sha256`,
   `after.sha256` and the all-files manifests. The manifests exclude only TUnit's `TestResults/`, which every run rewrites.
3. Require byte-identical `TerraScale.DotNet.dll`, `TerraBase.E2E.dll`/`.pdb` and runtimeconfig, an identical file set, a
   changed server, and **exactly** `./terrabase.dll` and `./terrabase.pdb` as the differing files. Any other difference
   (including `terrabase.xml`) stops the run.
4. Run the 3/3 smoke on each arm, then the control. Both run outside timing, and manifests are re-verified after each.
5. Reserve the other three heavy slots (`flock -n` on `/tmp/ts-heavy-{1..4}.lock`; the job's own slot is held by ts-heavy). If
   that fails, exit 73 with no timed arm. Then start the 1 Hz CPU observer and `vmstat`.
6. Run six alternating pairs (B A, A B, B A, A B, B A, A B). Each arm is one fresh `dotnet exec` process on the keyed api read
   lane: small (256 B) and medium (4 KiB), c1 and c16, a 10 s warmup and a 5 s window per cell, one repeat.
   - Before every arm, the script re-verifies all manifests and SDK/E2E/runtimeconfig identity, and checks that no earlier arm,
     smoke or control process is alive.
   - A failing arm stops the run. Nothing is retried or dropped.
7. Write `summary.md` and `artifacts.sha256`. The engine denominator and every metric keep their existing meanings (the
   harness and `ServerAttribution` are unchanged).

## Predeclared rules (fixed in the summary script)

- **Quiet gate**, per arm: external CPU mean ≤ 25 %, p95 ≤ 100 % of one core, no external build process ≥ 10 % in any sample.
  If any arm fails, the run is INVALID and gets no timing verdict. There are no opportunistic reruns.
- **Allocation REPRODUCED** only if B/op is lower in ≥ 5/6 pairs **and** the median paired Δ is negative, in **both** c16
  cells (small, medium).
  - NOT REPRODUCED: neither c16 cell has more than 3/6 lower with a negative median.
  - UNRESOLVED: anything else.
  - The c1 cells are reported beside the verdict, not pooled into it. No size of the saving is predicted.
- **Worsening CONCERN**, in any cell: any metric worse in ≥ 5/6 pairs with a worse median. The metrics are B/op, CPU µs/op,
  ops/s, and client, server, engine and overhead p50/p99. The absence of a CONCERN is not parity, and no parity or
  non-inferiority claim is made.
- **small/c1 slow-state flag**, as in common-r2: B/op ≥ 2,000 or CPU/op ≥ 25 µs above the median of all 12 small/c1 arms.
  Flagged arms stay in every table.
- **Sample counts**, reported for every arm and cell:
  - client samples, server requests and server sampled;
  - whether the 16,384-sample reservoir subsampled;
  - how many samples lie above each p99. Both quantiles take `sorted[min(n−1, ⌊0.99·n⌋)]`, so a full reservoir leaves 163
    samples above its p99.

## Token: how the timed lane obtains it, and the control

`VsTursoBenchmark` mints one token per arm process with `node.Issuer.Mint(keyId, …)`. It is the harness's `TestIssuer`:

- ES256 key with a ULID `kid`, `typ` `at+jwt`;
- `iss`/`aud` of the test issuer, `iat`/`nbf`/`exp` (12 h);
- claims `sub`, `project_id`, `scope` (four scopes), `key_id`, `key_gen` (16 hex), and `org_id`, which `Mint` adds.

It is a synthetic test-issuer API-key token. No production token, credential or tenant is involved. The harness never prints
its length.

Every field in that token has a fixed width (ULIDs, 10-digit times, 16-hex `key_gen`, a 64-byte ES256 signature). The source
therefore predicts one length for every mint, roughly 670–680 ASCII characters. That is under the candidate's 1,024-byte stack
buffer. This estimate is **unverified**: it depends on IdentityModel's JSON writer, for example whether `+` is escaped. It says
nothing about the size of real ts-auth tokens.

`api-auth-token-key-control.cs` runs once, outside timing, from a temp directory that no `Directory.Build.*` file reaches. It
does two things:

1. It loads each frozen arm's own `terrabase.dll` in its own `AssemblyLoadContext` and calls `ValidatedTokens.KeyOf` by
   reflection. The result must equal the baseline expression (or throw the same exception type) for:
   - null, empty, ASCII and Unicode BMP/astral strings;
   - unpaired high and low surrogates, a trailing high surrogate and a reversed pair;
   - UTF-8 lengths 1,023, 1,024 and 1,025, reached with 1-, 2-, 3- and 4-byte and lone-surrogate final characters;
   - oversized inputs of 4 KiB, 64 KiB and 300,000 B;
   - 32 minted control tokens.

   Any difference fails the run before timing. This is benchmark-only. No helper-only service test is added.
2. It mints 32 tokens by replicating `TestIssuer.Mint` and the benchmark's descriptor, with the arm's own IdentityModel and
   `EntityIds`. For those tokens it prints UTF-8 byte lengths, UTF-16 char lengths, segment count, header member count and
   payload claim count, and never a token. These tokens are **not** the timed token. Their lengths are reported as
   source-replicated and unverified against it.

## Validation done before launch (no timing)

- `bash -n` on the run script.
- The summary was run against a synthetic fixture directory (fake numbers, nothing timed). It rendered the gate, hashes,
  sample counts, verdict, CONCERN and control sections.
- The control ran once through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19`, read-only against an
  earlier job's frozen arm pair, `fix-api-etag-reuse-…/benchmark/bench/etag-pairs-delta/{before,after}`. **Both of those arms
  contain the old `KeyOf`.** The run checks the control's own mechanics, not this candidate: compile, per-arm load,
  reflection and minting. Results:
  - exit 0;
  - key equivalence PASS on all 56 inputs;
  - control tokens 674 UTF-8 bytes = 674 UTF-16 chars in all 32 mints;
  - 3 segments, 3 header members, 11 payload claims.

  The control file it ran is blob `3cede7c881129890d2118151a68d27cc2ac3bd04`, the committed one. That run used a branch head
  that was later restacked; the file did not change. An earlier attempt failed in the control's own `dynamic` binding before
  printing anything, and that bug is fixed. Output: delta `~/remote-builds/terrascale/fix-api-auth-temporary-allocation-ab-16110804…/benchmark/bench/token-key-control-check-{1,2}/`.
- Not run: any build of either arm, the smoke, the control against the candidate's arms, and any timed arm.

## Launch (after approval only; not run)

```
TS_HEAVY_HOST=delta /home/mariogk/.local/bin/ts-heavy-any nice -n 19 benchmark/scripts/api-auth-temporary-allocation-20261028.sh r1 --repo terrascale --branch fix/api-auth-temporary-allocation-ab
```

Run it once, from the committed branch head the parent approves. The label `r1` must be fresh, because the script refuses an
existing run directory. ts-heavy-any supersedes only a job with the same branch key, so make sure no job for this branch is
running. Results go to delta
`~/remote-builds/terrascale/fix-api-auth-temporary-allocation-ab-<hash>/benchmark/bench/auth-temporary-allocation-r1/`. The
build tree and SDK clone stay at `~/remote-builds/terrascale-ab/auth-temporary-allocation-r1` and
`~/remote-builds/ts-dotnet/auth-temporary-allocation-r1`.

## Status outside this run

- The original server-over-engine < 10 % target stays unmet.
- CORS and the cached system-store open remain HELD.
- Regional authenticated parity is incomplete.
- The owned database `api-perf-01m610ttq4n8ew5y4q25aj5sc2` stays `reconcile_required`. This run does not touch it.
- No production, live, beta or credentialed call is part of this run.
