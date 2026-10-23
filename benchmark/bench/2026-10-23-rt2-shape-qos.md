# RT2 lane L: shape QoS and N = 1000 on one dev host

`shape-bench load -steps 1,10,100,1000`, local two-node host (gru + sjc in one process), mariogk-dev (46 CPUs, shared
with other lanes), 3 repeats each, median (range). Readers and writer use the bench host's project token; its
organization is on the **enterprise** plan (`tests/TerraBase.E2E/Cluster.cs:830`), so this bench does not exercise the
Hobby request budget. The Hobby QoS changes are proven by integration tests (section 3).

| run | binary | start (UTC), host load |
| --- | --- | --- |
| before | `2558412fb` (rt2/free-reads-5) | r1 04:59 (36.0), r2 05:07 (27.3), r3 05:16 (14.9) |
| after | `77ad109f8` (rt2/shape-qos; same tree as rt2/shape-qos-2 `8a2955266`) | r1 05:58 (25.4), r2 06:07 (12.3), r3 06:15 (13.3) |

## 1. N = 1000 now runs to the end of the window

| N | run | writes done / 600 | delivered | p50 / p99 ms | 503 storage_busy | 429 shape_busy | point reads | shape reads |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | before | 600 | 600 | 1998 / 4563 | 0 | 0 | 9,760 (9,721–9,784) | 43,405 (39,620–45,786) |
| 1 | after | 600 | 600 | 696 / 1,251 | 0 | 0 | 3,853 (3,840–3,873) | 99,398 (98,155–106,683) |
| 10 | before | 600 | 6,000 | 3,052 / 7,512 | 0 | 0 | 14,768 (14,343–15,923) | 35,737 (32,884–40,128) |
| 10 | after | 600 | 6,000 | 707 / 1,326 | 0 | 0 | 3,946 (3,934–3,971) | 110,873 (108,640–112,504) |
| 100 | before | 600 | 60,000 | 2,224 / 6,169 | 0 | 0 | 78,584 (69,832–95,287) | 98,716 (80,938–111,515) |
| 100 | after | 600 | 60,000 | 723 / 1,249 | 0 | 0 | 4,000 (3,975–4,009) | 342,466 (284,999–352,087) |
| 1000 | before | **332 (220–380), stopped** | 760 (673–3,838) | 24,838 / 28,099 | 54 (24–55), step aborted | 26,870 (25,390–27,937) | **444,959 (412,618–455,705)** | 55,668 (54,790–58,781) |
| 1000 | after | **600 (all 3)** | 9,249 (7,442–26,229) | 1,479 / 29,524 | **0** | 378,047 (367,763–380,491) | **4,050 (4,029–4,055)** | 1,210,336 (1,207,967–1,219,579) |

- Before: every repeat aborted mid-window on `503 storage_busy` (offsets 20, 439, 27). After: no 503 in any repeat;
  all 1000 readers admitted and all 600 writes done.
- `point` reads at N = 1000: 445k → 4k (110× fewer), flat across N. At N = 1: 9.8k → 3.9k.
- Each run still exits 1 on the tool's V3 bound, which fails at N = 1 in every run before and after (the free-reads
  report, section 3, explains why that bound is the wrong gate). No other step failure.
- Shape metadata reads rose because readers poll more often: 3–5× more 200s at N ≤ 100 at a third of the latency.

## 2. Root cause of the `point` reads (section 5 of the free-reads report)

Not the shape read path. Measured with a stack probe on every `point` read (not committed):

1. **Project-token lookup, uncached.** Every project-token request resolved its project's organization and binding
   in the catalog twice (QoS admission, then authorization), `EngineManagementService.ProjectTokens.cs:15`: 8–10
   `point` reads per request, against 0.25–0.5 for an API key. The bench uses a project token, so each poll and each
   `shape_busy` retry cost about 8 system-store reads. 25k retries per step put 400k+ reads on the system store, whose
   admission answered 503.
2. **Cache stampede after a catalog commit.** The plan, database and record caches are tagged with the catalog
   commit sequence. After any commit, every in-flight request missed together and each re-read the same rows: on
   Hobby, 200 concurrent behind-the-head polls cost 636 `point` reads; after the fix, 0–12.

Fix (`rt2/shape-qos-1`): the project lookup uses the same commit-sequence cache as the database lookups, and concurrent
misses of one key at one sequence share one read (`ShareMissAsync`). No cap changed.

## 3. QoS (`rt2/shape-qos-2`), proven by Fast integration tests

DECISION: both the refund and a separate write lane. The refund alone cannot protect the writer: when 1000 readers
re-poll at once after a wake, most of those polls start origin flights or are refused before they can join one, so
they still spend the shared bucket. The test below fails with the refund alone and one shared lane (writer answered
429), and passes with separate lanes.

- `Closed_chunk_cache_hits_give_back_their_request_token_and_origin_page_reads_keep_it`: on Hobby with a stopped clock,
  150 sequential closed-chunk cache hits (over the burst of 100) all answer 200; sequential snapshot-page reads (origin
  work, not cached) answer 429 `tenant_busy` after at most 100.
- `Parked_readers_that_join_a_flight_give_back_their_request_token`: 3 waves of 64 parked live readers (192 polls on a
  burst of 100 that never refills) all answer 200; only each flight's starter pays.
- `A_hobby_writer_is_not_refused_while_a_thousand_readers_are_parked`: 1000 live readers on Hobby park, wake, then
  re-poll and retry on 429 without pause; 20 writes in that window all answer 201.
- `A_reader_fan_out_after_a_catalog_commit_reads_the_catalog_once_instead_of_once_per_reader` and
  `Project_token_requests_reuse_the_project_lookup_while_the_catalog_is_unchanged` cover section 2.

Without each fix, its test fails (checked by reverting each change: refund → the two token tests fail; one shared
lane → the writer test fails; no shared miss → the fan-out test fails; no project cache → the project-token test fails).

## 4. What still limits N = 1000 (not fixed here)

Delivery at N = 1000 is 1.2–4.4 % of 600,000. Readers that fall behind read closed chunks at different offsets and
hit the 8-running-flights-per-organization cap: 378k `429 shape_busy` per step, each retried after 200 ms, and each
refusal still resolves the lineage first (about 3 `shape` metadata reads), so metadata reads reach 1.2M per step.
Raising the cap is not shown to be right by this measurement. Next candidates: refuse a flight before lineage and
metadata reads, and serve behind-the-head readers through the closed-chunk cache without a flight.

The Hobby-plan live-cell run of the free-reads report (section 6) was not repeated: the cells need a plan window from
lane B.

## Commands

```sh
TS_HEAVY_HOST=mariogk-dev ~/.local/bin/ts-heavy-any bash -c 'make terrabase-build CONFIGURATION=Release && dotnet build tests/TerraBase.E2E -c Release && cd benchmark && for r in 1 2 3; do go run ./cmd/shape-bench load -steps 1,10,100,1000 -out ../bench-out/after2-r$r; done' --repo terrascale --branch rt2/shape-qos
```
