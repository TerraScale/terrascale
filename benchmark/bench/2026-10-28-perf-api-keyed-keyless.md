# Keyed-minus-keyless API attribution diagnostic, 2026-10-28

This diagnostic measures how much of the remaining API overhead the API-key path (key_id/key_gen) is responsible for,
before any shared-watch code is written. It is not a candidate patch. The keyless token is a synthetic, test-only
diagnostic and is not production-equivalent. The keyed-minus-keyless difference is not a production speedup.
The original server-over-engine <10% target remains unmet: engine p50 is ~4–7 µs, overhead ~84–116 µs on this host.

## Method

- One Release build of harness `f005f0081` (production source `93b873792`, `git diff` of src/tests/build props is empty;
  only benchmark wiring changed). SDK ts-dotnet `36d114877a69d18fba33b27ad362bd3e68e9e7ff`, cloned from GitHub at the pin.
  Build: 0 warnings, 0 errors. Both arms execute the same files, so server and SDK DLLs are byte-identical by construction.
  The checksums were re-verified before every arm:
  terrabase.dll `c1175b9eca103e93a8d1e36d83c5c9489c34ae4d3f0c058a71fa72f1e79c7fbf`,
  TerraScale.DotNet.dll `a4f3be7fccbf55ff5b5516cb5d0063982a67771fd47c2d45fe7cb982e641e19a`,
  TerraBase.E2E.dll `04199e143ce2833158a30d91c85ccaa379e2534acf4f4b18538a1ab26410308e`.
  The frozen key-watch artifacts (`ec66f5d7…`) were not reused: main's src has moved since then, and the harness needed the new lane.
- Lane `api` (keyed) vs `api-keyless`. Both tokens are minted in every process from one claim set with the same subject,
  project, org, scope and 12 h expiry. The keyless token omits only key_id/key_gen. The keyed synthetic key is granted
  through TestIssuer and performs setup in both arms. The keyless token is used only for measured reads. The arms differ in token
  bytes (~70 B shorter header), claim parsing, the key-authority lookup and the in-flight key watch. The delta covers all of these;
  it does not isolate the watch.
- beta, `ts-heavy-any nice -n 19`, `taskset -c 2-11` (10 CPUs), workstation concurrent GC, tiered PGO.
  Fresh processes ran in the order keyed/keyless, keyless/keyed, keyed/keyless (3 repeats per arm). Reads at 256 B/4 KiB, c=1/16,
  with a 10 s warmup and a 5 s window. The per-request attribution, engine denominator and independently sorted quantiles are
  unchanged (see ATTRIBUTION.md). CPU/op and B/op are whole-process figures (client, server and background), not server-only.
- Quiet control: the job held the other three heavy slots for all six arms, and no other job was stopped. A 1 Hz observer recorded
  external CPU over 416 samples: mean/p95/max 3.68/14.86/93.10 % of one CPU. There were no build/test samples >= 10 %. The
  two bursts (76.4 % in keyless-1, 93.1 % in keyless-3 at 09:54:55Z) were single seconds of the T3 server (`t3 serve`).
  The predeclared criteria (mean <= 25 %, p95 <= 100 %) pass. Per-arm external mean ranged 3.19–4.47 %.
- All six arms passed (1/1 test each, 0 skipped), with 24 cells and 0 operation errors. No arm or cell was discarded.

## Every repeat

| Arm | Pair | Size | c | ops/s | Client p50/p99 µs | CPU µs/op | B/op | Server p50/p99 µs | Engine p50 µs | Overhead p50/p99 µs | errors |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| keyed | 1 | small | 1 | 6090 | 153.1/343.4 | 223.7 | 30582 | 90.1/271.7 | 4.1 | 85.9/165 | 0 |
| keyed | 1 | small | 16 | 35401 | 342.1/2032.6 | 194.6 | 29994 | 112.6/1586.3 | 5.2 | 107.1/1562.1 | 0 |
| keyed | 1 | medium | 1 | 5784 | 161.7/374.2 | 226 | 46465 | 97.1/286.7 | 5 | 92.1/175.1 | 0 |
| keyed | 1 | medium | 16 | 29390 | 388.1/2114.6 | 216 | 45442 | 120.8/1627 | 6.8 | 113.8/1601.6 | 0 |
| keyed | 2 | small | 1 | 6033 | 154.3/342.3 | 226.7 | 30632 | 91.8/260.9 | 4.1 | 87.6/165.1 | 0 |
| keyed | 2 | small | 16 | 35086 | 346.7/2068.2 | 198.4 | 29982 | 114.9/1628.2 | 5.2 | 109.4/1592.7 | 0 |
| keyed | 2 | medium | 1 | 5645 | 166.1/393.1 | 232.3 | 46481 | 99.8/294 | 5 | 94.7/176.3 | 0 |
| keyed | 2 | medium | 16 | 28684 | 392.2/2203 | 222.5 | 45434 | 123.3/1689.1 | 6.7 | 116.4/1666 | 0 |
| keyed | 3 | small | 1 | 5968 | 157.3/357.3 | 229.5 | 30581 | 92.1/281.3 | 3.9 | 88.1/165.1 | 0 |
| keyed | 3 | small | 16 | 35419 | 345.9/1979.8 | 195.9 | 29911 | 113.8/1527.9 | 4.9 | 108.6/1509.5 | 0 |
| keyed | 3 | medium | 1 | 5700 | 163.1/406.6 | 229.4 | 46458 | 97.8/308.9 | 4.9 | 92.9/178.7 | 0 |
| keyed | 3 | medium | 16 | 28812 | 394.5/2161.3 | 222.1 | 45372 | 123/1674.8 | 6.5 | 116.3/1644.2 | 0 |
| keyless | 1 | small | 1 | 6206 | 149.8/323 | 219.1 | 28942 | 87.5/254.1 | 3.9 | 83.5/158.9 | 0 |
| keyless | 1 | small | 16 | 36905 | 328.6/2010.5 | 188.3 | 28325 | 108.6/1586.5 | 4.8 | 103.6/1571.3 | 0 |
| keyless | 1 | medium | 1 | 5813 | 160.9/379.1 | 225.4 | 44790 | 95.2/289.9 | 4.8 | 90.4/170 | 0 |
| keyless | 1 | medium | 16 | 30585 | 371.9/2061.8 | 210.8 | 43777 | 117.6/1595 | 6.1 | 111.3/1579.2 | 0 |
| keyless | 2 | small | 1 | 6183 | 150.9/339.4 | 219.7 | 28912 | 87.9/265.9 | 4 | 83.8/158.7 | 0 |
| keyless | 2 | small | 16 | 36557 | 331.8/2037.1 | 190.5 | 28321 | 109.4/1590.8 | 5 | 104.2/1562.7 | 0 |
| keyless | 2 | medium | 1 | 5832 | 160.2/377.2 | 223.5 | 44781 | 94.6/280.9 | 5 | 89.6/170.4 | 0 |
| keyless | 2 | medium | 16 | 30470 | 374.8/2117.7 | 214.5 | 43776 | 118.6/1615.6 | 6.3 | 112.2/1596.3 | 0 |
| keyless | 3 | small | 1 | 5117 | 160.5/372.8 | 277.1 | 34535 | 92.2/291.9 | 4.3 | 87.1/159.7 | 0 |
| keyless | 3 | small | 16 | 35818 | 343/2041.3 | 191 | 28443 | 110.3/1540.4 | 5.2 | 104.9/1511.9 | 0 |
| keyless | 3 | medium | 1 | 4766 | 168.8/440.7 | 287.5 | 58579 | 98.9/333.1 | 5.2 | 92.9/174.9 | 0 |
| keyless | 3 | medium | 16 | 30154 | 380.6/2112.3 | 216.1 | 43899 | 120/1639.3 | 6.5 | 113.3/1608.3 | 0 |

keyless-3 c=1 (both sizes) is an outlier: its B/op is +5.6/+13.8 KB and its CPU/op +57/+62 µs over the other keyless repeats.
That much allocation is not a key-path cost, and the same first-cell inflation appeared in the earlier delta run's keyed-1.
It is retained, which widens the keyless c=1 ranges.

## Keyed vs keyless, medians and full ranges of the three repeats


| Size | c | Metric | keyed median (min–max) | keyless median (min–max) | keyed−keyless median | ranges overlap |
| --- | ---: | --- | ---: | ---: | ---: | --- |
| small | 1 | Client p50 | 154.3 (153.1–157.3) | 150.9 (149.8–160.5) | +3.4 | yes |
| small | 1 | Client p99 | 343.4 (342.3–357.3) | 339.4 (323–372.8) | +4.0 | yes |
| small | 1 | Server p50 | 91.8 (90.1–92.1) | 87.9 (87.5–92.2) | +3.9 | yes |
| small | 1 | Overhead p50 | 87.6 (85.9–88.1) | 83.8 (83.5–87.1) | +3.8 | yes |
| small | 1 | Overhead p99 | 165.1 (165–165.1) | 158.9 (158.7–159.7) | +6.2 | no |
| small | 1 | Engine p50 | 4.1 (3.9–4.1) | 4 (3.9–4.3) | +0.1 | yes |
| small | 1 | CPU µs/op | 226.7 (223.7–229.5) | 219.7 (219.1–277.1) | +7.0 | yes |
| small | 1 | B/op | 30582 (30581–30632) | 28942 (28912–34535) | +1640.0 | yes |
| small | 16 | Client p50 | 345.9 (342.1–346.7) | 331.8 (328.6–343) | +14.1 | yes |
| small | 16 | Client p99 | 2032.6 (1979.8–2068.2) | 2037.1 (2010.5–2041.3) | -4.5 | yes |
| small | 16 | Server p50 | 113.8 (112.6–114.9) | 109.4 (108.6–110.3) | +4.4 | no |
| small | 16 | Overhead p50 | 108.6 (107.1–109.4) | 104.2 (103.6–104.9) | +4.4 | no |
| small | 16 | Overhead p99 | 1562.1 (1509.5–1592.7) | 1562.7 (1511.9–1571.3) | -0.6 | yes |
| small | 16 | Engine p50 | 5.2 (4.9–5.2) | 5 (4.8–5.2) | +0.2 | yes |
| small | 16 | CPU µs/op | 195.9 (194.6–198.4) | 190.5 (188.3–191) | +5.4 | no |
| small | 16 | B/op | 29982 (29911–29994) | 28325 (28321–28443) | +1657.0 | no |
| medium | 1 | Client p50 | 163.1 (161.7–166.1) | 160.9 (160.2–168.8) | +2.2 | yes |
| medium | 1 | Client p99 | 393.1 (374.2–406.6) | 379.1 (377.2–440.7) | +14.0 | yes |
| medium | 1 | Server p50 | 97.8 (97.1–99.8) | 95.2 (94.6–98.9) | +2.6 | yes |
| medium | 1 | Overhead p50 | 92.9 (92.1–94.7) | 90.4 (89.6–92.9) | +2.5 | yes |
| medium | 1 | Overhead p99 | 176.3 (175.1–178.7) | 170.4 (170–174.9) | +5.9 | no |
| medium | 1 | Engine p50 | 5 (4.9–5) | 5 (4.8–5.2) | +0.0 | yes |
| medium | 1 | CPU µs/op | 229.4 (226–232.3) | 225.4 (223.5–287.5) | +4.0 | yes |
| medium | 1 | B/op | 46465 (46458–46481) | 44790 (44781–58579) | +1675.0 | yes |
| medium | 16 | Client p50 | 392.2 (388.1–394.5) | 374.8 (371.9–380.6) | +17.4 | no |
| medium | 16 | Client p99 | 2161.3 (2114.6–2203) | 2112.3 (2061.8–2117.7) | +49.0 | yes |
| medium | 16 | Server p50 | 123 (120.8–123.3) | 118.6 (117.6–120) | +4.4 | no |
| medium | 16 | Overhead p50 | 116.3 (113.8–116.4) | 112.2 (111.3–113.3) | +4.1 | no |
| medium | 16 | Overhead p99 | 1644.2 (1601.6–1666) | 1596.3 (1579.2–1608.3) | +47.9 | yes |
| medium | 16 | Engine p50 | 6.7 (6.5–6.8) | 6.3 (6.1–6.5) | +0.4 | yes |
| medium | 16 | CPU µs/op | 222.1 (216–222.5) | 214.5 (210.8–216.1) | +7.6 | yes |
| medium | 16 | B/op | 45434 (45372–45442) | 43777 (43776–43899) | +1657.0 | no |


## Findings

- Allocation: keyed costs +1,640–1,675 B/op (~5.5 %) in every cell. Ranges do not overlap at c=16, and at c=1 they overlap only
  because of the retained keyless-3 outlier. This is the most consistent signal.
- Server overhead p50: keyed adds +2.5 to +4.4 µs over a keyless overhead of 84–112 µs (~3–4 %). The ranges are disjoint at c=16
  for both sizes and overlap at c=1. Overhead p99 at c=1 is +6 µs (disjoint). At c=16, p99 overlaps for small and is +48 µs
  overlapping for medium.
- CPU/op: +4.0 to +7.6 µs/op (2–3.5 %). Ranges are disjoint only for small c=16.
- Client p50: +2 to +17 µs. Ranges are disjoint only for medium c=16. Client p99 overlaps in every cell.
- The key path is measurable but small. Removing all of it would leave ~84–112 µs of overhead, ~20x the engine time.
  It cannot explain the gap to the <10% target. The latency mechanism of the remaining overhead is not established
  by this diagnostic.

## Recommendation

Profile the keyless overhead first (the ~84–112 µs common to both arms). It dominates, and a shared watch per token cannot
recover more than the ~3–4 µs / ~1.6 KB measured here (less its own cost). Do not implement a shared watch for latency.
If the allocation delta later justifies it, qualify it as an isolated candidate with these constraints:

- The same admission checks.
- Key entries by token hash, not key ID.
- Bounded entries with a per-request fallback.
- Cancel and retire a watch before any reuse.
- The same 2 s Fresh + 250 ms bound, Until deadline and fail-closed refusal.
- The PG renewal path unchanged.
- Revocation tests over established requests/feeds.
- The same keyed-only before/after pairs used here.

## Limits

- Instrumentation (activity listener, engine decorator, reservoir) perturbs both arms equally. Values are not uninstrumented.
- Single host (beta, 10 pinned CPUs). Absolute values are not comparable to the earlier delta runs, and this is not live
  gru/sjc/fra evidence.
- The test issuer has no network round trip for key status. Production keyed requests may differ in the cache-miss tail.
- With only three repeats per arm, a disjoint range is weak evidence, not a significance test.

Raw artifacts on beta: `/home/mariogk/remote-builds/terrascale/fix-api-keyed-keyless-diagnostic-1720f17fb2ad64c0a1ff24bf490ba4c12df402f3b30f6b7337e4d8c7bdac56b4/benchmark/bench/keyed-keyless-b1`.
They include the six `.md/.log/.host` files, `cpu-processes.jsonl`, `vmstat.txt`, `artifacts.sha256`, source/SDK/harness SHAs,
the reservation receipt, the build log and the exact `bin/`. Reproduce with `ts-heavy-any nice -n 19
benchmark/scripts/api-keyed-keyless-20261028.sh <label> --repo terrascale --branch fix/api-keyed-keyless-diagnostic`.
