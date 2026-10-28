# Cached system-store open: predeclared paired measurement, run r1 aborted before timing (2026-10-28)

Candidate `fix/terrabase-cached-system-open` @ 8aaef93e8 (production delta: `TursoStorageEngine.OpenSystemDatabaseAsync`
fast path and `StampingStorageEngine`'s cached system wrapper), against main a42f65d55. The common-overhead profile
(`fix/api-common-overhead-profile`, `2026-10-28-perf-api-common-profile.md`) attributes about 356 B/request to this path.

**Result: no measurement. The allocation effect is still unmeasured.** The one predeclared run (r1) stopped at its own
pre-timing artifact check: `TerraBase.E2E.dll` differed between the before and after builds, and the plan required it to be
byte-identical. No timed arm started, so there are no B/op, CPU, throughput or latency numbers, and no verdict.

## Plan (committed at c67903c38 before the launch)

`benchmark/scripts/api-cached-system-open-20261028.sh` and `api-cached-system-open-summary.py`. The plan built both Release
E2E arms against ts-dotnet 36d114877a69d18fba33b27ad362bd3e68e9e7ff. It required `TerraScale.DotNet.dll`, `TerraBase.E2E.dll`
and the runtimeconfig to be identical, with only `terrabase.*` allowed to differ. Then six alternating fresh-process pairs
(B A, A B, B A, A B, B A, A B) of the keyed api read lane: small and medium documents at c1 and c16, a fixed 10 s warmup and
5 s window, `taskset -c 2-11`, workstation concurrent GC with TieredPGO. The quiet gate and the verdict rules are in the
summary script: B/op REPRODUCED when both c16 cells fall in at least 5 of 6 pairs with a negative median, a worsening CONCERN
flag, and a small/c1 slow-state flag.

## What happened

| Item | Value |
| --- | --- |
| Command | `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-cached-system-open-20261028.sh r1 --repo terrascale --branch fix/terrabase-cached-system-open-ab` |
| Harness head | c67903c38a5025712dfaed5ec47257cad25181ce (8aaef93e8 + `benchmark/scripts/` only) |
| Host state at launch | beta idle, all four heavy slots free, load 0.16 |
| Steps passed | SDK pin, Turso pins (`SHA256SUMS` before and after download), `make restore`, both Release builds (0 warnings, 0 errors), identical `TerraScale.DotNet.dll` (a4f3be7f…41e19a) and runtimeconfig (5628e9d5…0809a) |
| Failed check | `cmp before/TerraBase.E2E.dll after/TerraBase.E2E.dll`: differ at byte 137 |
| before `terrabase.dll` / `TerraBase.E2E.dll` | 54e1578ce84105e9f54b836d30470fed6eed58375fb9a5188f1e6b6609cd74af / a28fac0aa00b7747328fe4322f731717dab1e0a18b0938ea16a0f07adbaf5e2d |
| after `terrabase.dll` / `TerraBase.E2E.dll` | 6868fb0b561c7a24fcbecaed5c5a3a708a5c3aab20a066dc8ab7c96459476207 / 2eadb593cea6a3056e081a06bcbc3476fbe19a40f40a53db1eeb289c4331651e |
| Other differing files | `terrabase.pdb`, `TerraBase.E2E.pdb`; nothing else in the all-files manifests |
| Timed arms, reservation, observer | none started |
| Raw | `beta:~/remote-builds/terrascale/fix-terrabase-cached-system-open-ab-425dad0ed56916cf56224d19eb2e76fc3465d797f40393a72b60c1d7f7ed1e48/benchmark/bench/cached-system-open-r1/` |
| Raw hashes | `job.log` f2d65167…48754d, `production.diff` 19845e95…3f8, `before.all.sha256` f9522cae…a38e, `after.all.sha256` 657fadf6…d72d0d |

The two `TerraBase.E2E.dll` files are the same length (2,482,688 bytes) and differ in 72 bytes, in four clusters: 4 bytes
at offset 136 (the COFF timestamp field), 16 bytes near 2,239,924, and 4 + 48 bytes near 2,479,616–2,479,715. The offsets fit
the deterministic identity and debug fields (timestamp, MVID, debug-directory timestamp, CodeView GUID and PDB checksum).
This is an inference from offsets; the PE was not parsed field by field. A likely cause is that the E2E project compiles
against the server's reference assembly, and the candidate adds members (`Store.Inner`, the renamed async method's state
machine), which changes that reference and so the E2E identity. The ClientCors run kept E2E identical because it changed
only a method body. This cause is not verified.

## Open decision (not made here)

The predeclared identity rule cannot hold for this candidate as written. Two ways to measure it, each a plan change for the
root to approve before a new single run:

1. Keep separate E2E builds and replace `cmp` with a check that the E2E files are equal except the identity and debug
   fields (parsed, not by offset), still requiring identical IL and metadata otherwise.
2. Build `TerraBase.E2E.dll` once and run the same copy in both arms, so only `terrabase.dll` differs. This needs a check
   that the E2E binary binds to both servers (it uses none of the changed members).

## Limits

- Not measured at all: no allocation, CPU, throughput or latency result exists for this candidate.
- Even when measured, this lane is beta only, loopback, single node, api read only. The <10 % server-over-engine target
  stays unmet and untouched, and there is no live gru/sjc/fra evidence.
