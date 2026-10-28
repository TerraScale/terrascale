# Engine writes: the intent journal stops toggling change capture, 2026-10-28

## Change

A client writing one document at a time records and retires a node-local write intent around each write: two
`_intents.db` transactions beside its data commit (decision 0013d). The journal's store opens with SYSTEM's name for
SYSTEM's semantics, so `TursoDatabaseStore.Captures` (`name != _intents`) was always true. Each intent transaction
therefore ran `PRAGMA capture_data_changes_conn('off')` before its statement and `…('after')` after it. Turso makes a
connection prepare its statements again after such a setting. #612 meant to remove these toggles and never did. The
journal is now known by its file name.

A diagnostic count on beta (scratch branch, not merged) found, per c=1 small engine write: 3 `BEGIN IMMEDIATE`/`COMMIT`,
2 capture-off and 2 capture-on statements, and fresh prepares of the intent statements.

## Method

- Host **delta**, 16 vCPU, .NET 11.0.0, production workstation concurrent GC and tiered PGO through `run.sh`.
- One `ts-heavy-any` job on `fix/intent-journal-no-capture` built both arms: the base is `origin/main` `6475a24d7` in a
  side worktree, the fix is that commit plus this change.
- Arms alternated per repeat (base r1, fix r1, base r2, …), three repeats, 1 s warmup and 5 s cells, 10,000 preloaded
  keys per size.
- Lanes:
  - `turso` and `turso-cdc`: raw controls, untouched by the change.
  - `engine`: in-process `IDocumentService`.
  - `wire`: the authenticated wire protocol over 16 loopback connections.
- FULL commits, admission, metering and capture stay on. Tables give medians (min–max). 0 errors in every cell.
- Host load 0.3–2.7 during the run, IO PSI some avg60 at most 5.7%. The raw controls stay within their noise between
  arms, so the host did not drift between them.
- A first run on beta is not used. IO pressure from another job reached 23%, and even raw Turso fell from about 550 to
  about 200 ops/s in two of its repeats.

```sh
TS_HEAVY_HOST=delta ts-heavy-any bash -c '<build base worktree at 6475a24d7; for r in 1 2 3; for arm in base fix:
  TERRABASE_VS_TURSO_LANES=turso,turso-cdc,engine,wire TERRABASE_VS_TURSO_WORKLOADS=write TERRABASE_VS_TURSO_REPEATS=1
  TERRABASE_VS_TURSO_CONCURRENCY=1,16 bash benchmark/dotnet/VsTurso/run.sh <out>>' --repo terrascale --branch fix/intent-journal-no-capture
```

## Results

| Size | c | Lane | Ops/s before | Ops/s after | p50 µs before → after | p99 µs before → after | CPU µs/op before → after | Alloc B/op before → after |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| small | 1 | turso | 919 (908–1,136) | 876 (867–938) | 1,011 → 1,071 | 3,633 → 3,954 | 519 → 549 | 4,663 → 4,677 |
| small | 1 | turso-cdc | 852 (842–878) | 868 (855–934) | 1,082 → 1,068 | 3,863 → 4,003 | 576 → 563 | 4,689 → 4,671 |
| small | 1 | engine | 407 (371–408) | 530 (513–568) | 2,053 → 1,473 | 9,496 → 8,378 | 2,332 → 1,653 | 112,493 → 105,563 |
| small | 1 | wire | 380 (341–382) | 470 (435–550) | 2,358 → 1,846 | 8,596 → 8,023 | 2,600 → 1,910 | 118,272 → 111,429 |
| small | 16 | turso | 848 (844–869) | 846 (838–849) | 19,232 → 19,144 | 31,095 → 30,738 | 624 → 628 | 5,109 → 5,090 |
| small | 16 | turso-cdc | 808 (797–810) | 790 (784–797) | 20,296 → 20,484 | 35,294 → 37,919 | 640 → 657 | 4,930 → 4,929 |
| small | 16 | engine | 1,891 (1,884–1,946) | 2,089 (2,067–2,305) | 6,820 → 6,250 | 21,230 → 19,104 | 487 → 432 | 68,660 → 67,745 |
| small | 16 | wire | 1,930 (1,914–1,984) | 1,962 (1,955–2,042) | 6,961 → 6,492 | 20,590 → 23,062 | 575 → 577 | 75,012 → 74,230 |
| medium | 1 | turso | 851 (829–933) | 856 (843–889) | 1,037 → 1,015 | 4,257 → 4,843 | 593 → 565 | 20,056 → 20,060 |
| medium | 1 | turso-cdc | 823 (811–837) | 807 (763–821) | 1,032 → 1,090 | 4,780 → 4,204 | 602 → 625 | 20,233 → 20,228 |
| medium | 1 | engine | 364 (361–429) | 478 (472–526) | 2,562 → 1,661 | 10,335 → 9,109 | 2,533 → 1,747 | 216,841 → 210,034 |
| medium | 1 | wire | 374 (328–384) | 443 (390–461) | 2,198 → 1,864 | 9,597 → 9,152 | 2,471 → 1,980 | 257,585 → 250,738 |
| medium | 16 | turso | 759 (753–764) | 784 (777–791) | 20,805 → 20,065 | 41,938 → 41,721 | 744 → 706 | 20,592 → 20,575 |
| medium | 16 | turso-cdc | 717 (707–722) | 732 (718–739) | 21,838 → 21,384 | 43,433 → 45,156 | 786 → 752 | 20,426 → 20,421 |
| medium | 16 | engine | 1,524 (1,501–1,567) | 1,740 (1,649–1,741) | 8,362 → 7,381 | 29,479 → 28,180 | 604 → 516 | 173,611 → 172,730 |
| medium | 16 | wire | 1,568 (1,549–1,594) | 1,568 (1,552–1,616) | 8,528 → 8,241 | 26,595 → 30,852 | 686 → 676 | 214,426 → 213,611 |

- **c=1 engine writes:** 30–31% more throughput, p50 −28% (small) and −35% (medium), CPU per write −29% (about 680 µs less).
- **c=1 wire writes:** 24% (small) and 18% (medium) more throughput, p50 −22% and −15%.
- **c=16:** group commit already shares one intent across a batch. Engine writes gain 10–14%; wire writes are within
  noise (+2%, flat).
- **Q-PARITY:** single-writer wire p50 is still above 1.1 × (raw Turso + TCP echo). Small is 1,846 µs against about
  1,200 µs. What remains is the intent journal's other two commits and the data commit's bookkeeping statements;
  0015 lists the options.

## Correctness

The journal records and retires the same rows in the same transactions, with the same sync settings. Only the capture
setting of its own connection changes. It is never replicated, backed up or rewritten, and nothing reads its
`turso_cdc`.

- Full TerraBase Fast on mariogk-dev: 3,106/3,133 passed with the fix, against 3,114/3,133 on its base on the same host.
  Both runs failed on 2-minute timeouts under load 51/46.
- The ten tests that failed only with the fix each passed 3/3 on both arms when run alone.
- CI is the gate.
