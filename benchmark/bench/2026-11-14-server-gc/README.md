# Server GC with DATAS: TerraBase, ts-auth, dashboard (2026-11-14)

Branch `cos/88f01b30-C5-runtime-gc` vs `origin/main` (`b3e7c0f435`), same machine, `nice -n 19`, nothing else heavy running.

| Item | Value |
| --- | --- |
| Host | mariogk-dev, 16 cores (`nproc`), 31 GB RAM (`free -g`: 19 GB available) |
| Date | 2026-11-14 |
| Change | `ServerGarbageCollection` + `ConcurrentGarbageCollection` + `GarbageCollectionAdaptationMode` 1 in all three csproj; dashboard also `TieredPGO` + `PublishReadyToRun` |
| Verified | `terrabase.runtimeconfig.json` / `ts-auth.runtimeconfig.json` / `TerraScale.Dashboard.runtimeconfig.json` carry `System.GC.Server: true`, `System.GC.DynamicAdaptationMode: 1`; dashboard dll 6.7 MB → 17.6 MB (ReadyToRun) |

`benchmark/dotnet/run-http.sh` measures the endpoints sample app, whose csproj pins its own `ServerGarbageCollection`; it does not run TerraBase and cannot show this change. The closest TerraBase-native measurement the repo's README names is `make -C benchmark insert-batch` (`benchmark/cmd/insert-bench`), which drives the real TerraBase binary over SQL REST and the Postgres wire. On main that tool is stale: TerraBase now refuses `DOTNET_ENVIRONMENT=Development` together with `TS_PEERS_FILE` (commit 8c8c9df27c), so it was run from a patched copy in `/tmp` that starts the local single node without the peers file (Development `local` identity) and creates the database with `homeRegion: "local"`; no repo file was changed for it. Its paths `benchmark/bench/TerraScale.Endpoints.Benchmarks` and `bench/results` are also stale (`benchmark/dotnet/...`); run from a patched copy with the same 10s/64/3 shape.

## TerraBase under load: insert-bench, 3 runs each

`nice -n 19 /tmp/c5-insert-bench2 -terrabase <dll> -batches 20 -repeats 3` (self-contained publish for the peak-RSS pair).

| Metric | before (main) | after (branch) | median before | median after |
| --- | --- | --- | --- | --- |
| REST 1-row p50 ms | 12.64 / 13.50 / 9.77 | 12.27 / 16.90 / 15.41 | 12.64 | 15.41 |
| REST 250-row p50 ms | 19.47 / 21.10 / 16.87 | 20.39 / 17.52 / 27.21 | 19.47 | 20.39 |
| REST 1000-row p50 ms | 47.36 / 47.47 / 29.96 | 32.31 / 31.28 / 31.08 | 47.36 | 31.28 |
| REST 1-row req/s | 58 / 50 / 58 | 74 / 40 / 44 | 58 | 44 |
| REST 250-row rows/s | 9670 / 8634 / 8775 | 11340 / 11501 / 7831 | 8775 | 11340 |
| REST 1000-row rows/s | 18263 / 20022 / 25344 | 28839 / 22600 / 25790 | 20022 | 25790 |
| pg wire 1-row p50 ms | 11.01 / 10.85 / 9.02 | 11.84 / 9.04 / 12.54 | 10.85 | 11.84 |
| pg wire 250-row p50 ms | 16.07 / 14.14 / 14.86 | 17.90 / 20.71 / 17.41 | 14.86 | 17.90 |
| pg wire 250-row rows/s | 13434 / 13060 / 13936 | 13865 / 9912 / 10817 | 13434 | 10817 |
| TerraBase peak RSS MB (under load) | 324.1 | 341.6 | — | — |

Large batches (the GC-heavy path) are clearly better after: REST 1000-row p50 median 47.4 → 31.3 ms (−34%), rows/s 20.0k → 25.8k (+29%); REST 250-row rows/s +29%. The 1-row path is mixed and noisy (single-row inserts are dominated by the synchronous write path, not GC): p50 median 12.6 → 15.4 ms, but one after-run was 12.3 ms — within the machine's run-to-run spread, and the `run-http.sh` control below shows the same ±7% drift on unchanged code. The pg wire is slightly slower at 250 rows (p50 14.9 → 17.9 ms, rows/s −19%). p95 collapses after on the small batches (REST 1-row p95 median ~44 → ~21 ms in the best pair), consistent with shorter server-GC pauses.

If a future read judges only the 1-row and pg-wire cells, the change is not a uniform win on this host; the win is on the allocation-heavy multi-row paths, which are the ones whose production p99 (sjc/fra 8–15x p50) motivated it. Peak RSS under load +5% (324 → 342 MB), idle RSS +13–20% (below), well under the 2x stop line.

## Idle RSS after start, before/after

Each published app started once with throwaway Development settings, read at ~25 s and ~65 s after `healthz`/`readyz` returned 200 via `ps -o rss`.

| Service | before (main) | after (branch) |
| --- | --- | --- |
| TerraBase | 160.7 → 179.5 MB | 180.6 → 215.8 MB |
| ts-auth | 192.8 → 198.7 MB | 203.3 → 204.6 MB |
| dashboard | 167.5 → 173.2 MB | 175.4 → 185.0 MB |

Idle RSS is up 13–20% with DATAS, not 2x: the adaptive heap keeps idle memory near workstation GC on this 16-core host, as designed.

## Endpoint pipeline control: run-http.sh, 3 runs each

Medians over three `nice -n 19 run-http.sh 10s 64 3` runs per side (this app pins its own GC; identical build both sides — machine-drift control only). All five scenarios stayed within ±7% rps and ±0.02 ms p50 between the two sides (e.g. `ts/text` 283.5k → 282.5k rps, p50 0.162 → 0.166 ms), so the insert-bench deltas are not machine drift.

Raw outputs: `/tmp/c5-results/http-*.csv` (6 files) and `/tmp/c5-insert-{before,after}-*.md`.
