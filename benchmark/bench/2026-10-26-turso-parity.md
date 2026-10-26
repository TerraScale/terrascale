# RT7: Turso reuse and client parity, 2026-10-26

## Result and limits

The safe reuse change prepares one INSERT per table during encrypted logical copies. It does not change client reads
or writes. The client measurements below are **not parity proof**: every cell completed three repeats without errors,
but host contention produced large outliers. Preserve these failures and rerun on a quiet host before claiming parity.
In particular, the +12% / +20% write gaps in 0015a were measured on delta, not on this contended beta run;
these results cannot establish a regression from those numbers.

## Method

- Same harness and target definitions as [RT2/RT3c](2026-10-23-rt2-vs-turso.md): 10,000 preloaded keys per size,
  small 256 B and medium 4 KiB JSON, one-second warmup and five-second cells, three interleaved repeats.
- Host beta, 12 vCPU, .NET 11.0.0-rc.1.26425.128; production workstation concurrent GC and tiered PGO through run.sh.
  Baseline source `8b5227ee8`; native Turso 0.8.2 with the pinned qualified AEGIS/FTS runtime, verified SHA-256.
- Ran through `ts-heavy-any`, one cell at a time. Admission, metering, authentication and replication capture stay on.
  FULL commits remain enabled. The wire lane uses 16 persistent loopback TCP connections; HTTP is excluded.
- Lanes: raw in-process `turso`, captured `turso-cdc`, in-process document `engine`, authenticated `wire`, and `tcp-echo`.
  120 measured rows, zero operation errors. Tables show medians and min–max of the three repeats.
- Host load started near idle (0.09/12) and other jobs arrived during the run. The medium echo c=1 throughput fell
  from 39,661 to 3,483 ops/s in one repeat; wire medium c=16 ranged 5,386–81,511 ops/s. No outlier was discarded.

Reproduce (the wrapper selects/admit-checks a dev host; use the same host for all compared runs):

```sh
ts-heavy-any nice -n 19 env TERRABASE_VS_TURSO_LANES=turso,turso-cdc,engine,wire,tcp-echo \
  TERRABASE_VS_TURSO_WORKLOADS=read,write TERRABASE_VS_TURSO_REPEATS=3 TERRABASE_VS_TURSO_SECONDS=5 \
  bash benchmark/dotnet/VsTurso/run.sh benchmark/bench/rt7-parity-raw.md \
  --repo terrascale --branch refactor/turso-reuse-rt7
```

The native assets must first be downloaded and verified with scripts/vendor/download-turso-native.sh in the managed
checkout. A missing native asset stopped the first build; after that prerequisite the benchmark built and ran.

## Read target

For c=1, target p50/p99 = 1.1 × (raw Turso + bare TCP echo), evaluated for each percentile.
For c=16, target throughput = 0.9 × min(raw Turso, TCP echo). These are the standing Q-PARITY definitions.

| Cell | Target | Wire median | Observation under contention |
| --- | ---: | ---: | --- |
| small c=1 p50 | ≤ 52.9 µs | 41.8 µs | inside numerical target; repeat on quiet host |
| small c=1 p99 | ≤ 108.6 µs | 90.8 µs | inside numerical target; repeat on quiet host |
| small c=16 throughput | ≥ 186,412 ops/s | 128,147 ops/s | 31.3% below target |
| medium c=1 p50 | ≤ 62.0 µs | 59.9 µs | inside numerical target; repeat on quiet host |
| medium c=1 p99 | ≤ 123.6 µs | 135.2 µs | outside numerical target; repeat on quiet host |
| medium c=16 throughput | ≥ 109,991 ops/s | 78,366 ops/s | 28.8% below target |

## Measured cells

| Size | Workload | c | Lane | Ops/s median (min–max) | p50 µs median (min–max) | p99 µs median (min–max) | CPU µs/op | Alloc B/op |
| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| small | read | 1 | turso | 37,534 (24,242–42,925) | 24.7 (22.3–37.4) | 46.0 (33.2–70.4) | 26.6 | 2,170 |
| small | read | 1 | engine | 551,984 (516,757–623,608) | 1.6 (1.3–1.6) | 3.0 (2.5–3.3) | 1.8 | 1,048 |
| small | read | 1 | wire | 21,903 (20,307–22,594) | 41.8 (41.1–44.6) | 90.8 (80.3–98.7) | 57.5 | 3,859 |
| small | read | 1 | tcp-echo | 38,774 (37,496–40,929) | 23.4 (23.0–24.0) | 52.7 (39.5–53.0) | 37.0 | 911 |
| small | read | 16 | turso | 207,124 (191,860–240,257) | 46.1 (44.7–46.6) | 134.0 (60.5–704.2) | 47.9 | 2,164 |
| small | read | 16 | engine | 1,425,032 (1,048,138–1,640,640) | 2.8 (2.7–3.2) | 379.8 (214.4–437.0) | 3.8 | 1,049 |
| small | read | 16 | wire | 128,147 (93,010–143,756) | 86.5 (78.7–116.4) | 931.1 (863.4–1,241.1) | 46.8 | 3,865 |
| small | read | 16 | tcp-echo | 235,667 (229,718–253,530) | 48.1 (45.6–49.1) | 424.5 (366.9–429.1) | 21.3 | 904 |
| small | write | 1 | turso | 600 (238–805) | 1,607.8 (1,552.2–4,464.3) | 3,040.0 (2,253.5–11,331.1) | 431.0 | 4,869 |
| small | write | 1 | turso-cdc | 480 (214–579) | 1,773.4 (1,641.3–4,428.2) | 6,808.7 (2,351.0–11,583.1) | 523.5 | 4,776 |
| small | write | 1 | engine | 197 (123–303) | 3,729.8 (3,135.8–7,480.5) | 15,407.1 (11,828.7–20,975.3) | 2,786.0 | 112,516 |
| small | write | 1 | wire | 113 (54–306) | 8,087.5 (3,128.3–14,194.2) | 23,929.6 (10,196.7–56,539.2) | 3,264.6 | 120,464 |
| medium | read | 1 | turso | 28,293 (26,285–28,339) | 33.0 (33.0–34.2) | 58.6 (57.1–70.2) | 35.5 | 13,702 |
| medium | read | 1 | engine | 360,361 (354,284–363,581) | 2.3 (2.3–2.3) | 4.8 (4.5–4.8) | 2.8 | 4,912 |
| medium | read | 1 | wire | 15,036 (8,841–15,674) | 59.9 (58.2–80.2) | 135.2 (126.9–275.8) | 86.4 | 15,411 |
| medium | read | 1 | tcp-echo | 38,348 (3,483–39,661) | 23.4 (23.4–66.9) | 53.8 (47.8–4,915.3) | 37.0 | 912 |
| medium | read | 16 | turso | 122,212 (119,561–139,099) | 60.9 (60.7–62.1) | 1,558.3 (1,378.1–2,219.8) | 64.8 | 13,691 |
| medium | read | 16 | engine | 452,330 (151,540–473,547) | 5.0 (4.8–5.4) | 1,326.5 (52.6–1,383.7) | 7.4 | 4,892 |
| medium | read | 16 | wire | 78,366 (5,386–81,511) | 122.7 (121.7–559.3) | 1,381.8 (1,261.2–45,210.3) | 67.7 | 15,382 |
| medium | read | 16 | tcp-echo | 227,493 (219,635–236,597) | 49.7 (47.8–50.9) | 428.1 (427.1–458.9) | 21.7 | 904 |
| medium | write | 1 | turso | 510 (456–583) | 140.3 (139.2–1,591.1) | 9,740.0 (3,178.6–10,127.6) | 307.6 | 20,354 |
| medium | write | 1 | turso-cdc | 178 (169–217) | 4,988.7 (2,085.6–5,069.3) | 13,533.6 (11,338.3–48,008.0) | 727.3 | 20,422 |
| medium | write | 1 | engine | 116 (84–163) | 5,385.6 (3,493.9–7,716.6) | 24,315.5 (17,753.8–136,665.7) | 3,151.1 | 217,390 |
| medium | write | 1 | wire | 122 (98–161) | 6,531.6 (4,219.7–7,583.8) | 21,395.3 (19,413.3–120,552.2) | 3,332.6 | 259,687 |

The raw medium write's median p50 is 140 µs while its p99 is 9.7 ms; it is below the host's ordinary sync cost,
as also observed in the older RT2 report. This is unsuitable as a durable-write cost estimate. The captured baseline
has median p50 4,989 µs; the engine is 5,386 µs. The requested raw baseline is retained rather than silently replaced.

## What remains

- c=16 wire reads still spend CPU on frame/socket processing, admission, catalog/key freshness, memory ownership,
  metering and the answer, even when the document cache avoids native I/O. Small read engine CPU is 3.8 µs/op versus
  wire 46.8 and echo 21.3; medium is 7.4 versus 67.7 and 21.7. These are whole-process cell measurements, including
  client and background work, not additive server-only profiles. Generic Turso sync or SQL cannot remove these checks.
- c=1 small writes spend 2,786 µs CPU/op in the engine versus 431 raw; medium spends 3,151 versus 308, with
  112,516/217,390 B allocated per engine operation. This run cannot attribute that difference to a single function.
  Stored-state decode/canonical materialization/digests and feed/accounting statements remain on the current path;
  retaining corruption validation, LWW and capture is required. See 0015a's finer statement measurements.
- The copy optimization is intentionally outside these client paths. No claim that it closes a client gap follows
  from a faster copy. The ranked contract audit is [0018](../../docs/terrabase/decisions/0018-turso-reuse.md).
- **Needs format bump:** 0015a's feed sequence consolidation remains a parent handoff, about 8 µs/write estimated
  on its original host, under 1%. This PR leaves StorageFormat unchanged.

## Encrypted copy A/B

Measured on beta through `ts-heavy-any`, sequential arms, three repeats each; 100,000 rows in one encrypted
source table with an INTEGER PRIMARY KEY and 256-character Bogus-generated TEXT. `CopyBenchmark` creates the
same seeded payload in each process and asserts every target contains 100,000 rows. The source build is outside
the timer; each copy includes schema, bounded chunk commits and the final checkpoint. No client path runs here.
Baseline uses `TursoCopy.cs` from `8b5227ee8`, compiled with the same benchmark on the rebased tree; after is
`334425266`. The earlier successful baseline binary was reused after correcting the output path to an absolute one.

| Arm | Rows/s median (min–max) | Seconds median (min–max) | Allocated B/row median (min–max) |
| --- | ---: | ---: | ---: |
| Before: prepare per row | 15,451 (14,733–16,377) | 6.472 (6.106–6.788) | 2,026 (2,025–2,026) |
| After: native program per table | 225,858 (202,232–252,418) | 0.443 (0.396–0.494) | 1,480 (1,480–1,480) |

Observed throughput is 14.6× and allocation is 27.0% lower. These arms ran sequentially rather than alternating,
and the host was shared, so the wall-time ratio includes possible host drift. It is not a controlled estimate of
an isolated-host speedup. The change removes repeated native preparation and parameter construction by design;
client parity is unaffected. Peak working set below is a **process-lifetime** value including source generation,
not a per-copy memory measurement or proof that copy memory cannot regress.

| Arm | Repeat | Seconds | Rows/s | Allocated B/row | Process-lifetime peak working set B | Rows copied |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Before | 1 | 6.106 | 16377 | 2025 | 200224768 | 100000 |
| Before | 2 | 6.472 | 15451 | 2026 | 201093120 | 100000 |
| Before | 3 | 6.788 | 14733 | 2026 | 202899456 | 100000 |
| After | 1 | 0.494 | 202232 | 1480 | 196890624 | 100000 |
| After | 2 | 0.443 | 225858 | 1480 | 198733824 | 100000 |
| After | 3 | 0.396 | 252418 | 1480 | 199139328 | 100000 |

Run the copy benchmark through `ts-heavy-any` with an absolute `TERRABASE_TURSO_COPY_OUT` path and
`dotnet run --project tests/TerraBase.E2E --configuration Release --no-build --
--treenode-filter '/*/*/CopyBenchmark/*' --minimum-expected-tests 1` after building the selected arm.
The source and target fixture opens explicitly use ReadWriteCreate. Ordinary Fast coverage remains CI's gate.

## Diagnostic write profile attempt

One additional diagnostic run on beta at `334425266` used dotnet-trace 10.0.750501 with
Microsoft-DotNETCore-SampleProfiler: engine only, writes c=1, one repeat, ten seconds per size. It passed with
zero errors. The trace includes startup, preloading, runtime/background threads and both sizes. It reports sampled
thread time including blocked threads, **not exclusive CPU cost per write**, so it cannot isolate the medium CPU gap.
This is diagnostic evidence, not another parity repeat or evidence that a semantic digest is cheap to delete.

| Diagnostic cell | Ops/s | p50 µs | p99 µs | CPU µs/op | Alloc B/op |
| --- | ---: | ---: | ---: | ---: | ---: |
| small engine write c=1 | 190 | 3,592.3 | 30,960.8 | 3,410.1 | 114,542 |
| medium engine write c=1 | 57 | 15,397.9 | 77,017.9 | 6,964.4 | 221,541 |

| Selected function | Exclusive sampled thread time |
| --- | ---: |
| LowLevelThreadBlocker.TimedWait | 21.21% |
| LowLevelLifoSemaphore.Block | 18.87% |
| WaitSubsystem.ThreadWaitInfo.Wait | 18.16% |
| WaitHandle.WaitOneNoCheck | 10.02% |
| TursoWriteLane.Work | 6.61% |
| TursoInterop.StatementStep | 2.07% |
| TursoInterop.ConnectionPrepareSingle | 0.29% |
| DocumentStateEncoding.Digest | 0.02% |

An isolated-host CPU profile remains necessary before assigning the managed write gap to a particular function.
The audit establishes why native page checksums/MVCC/sync cannot safely replace the current logical obligations;
it does not turn this unsuccessful attribution attempt into a speed or protocol conclusion.
