# GitHub-hosted CI executor comparison

All durations below are seconds unless formatted as minutes. Job time includes setup and teardown; workflow wall clock includes scheduling. Every run used GitHub-hosted runners. No suites or checks were removed. These are CI observations, not controlled hardware benchmarks.

## Sequential PR runs and checkout identity

The branch application source was held at `847dd312c`, but that did **not** hold the tested code constant: actions/checkout tests the PR's synthetic merge with current main. The Docker baseline checked out `4dee422aeedd7a10e4597a02e15c0e8512cd5ec7` (797 auth Fast tests), native attempt 1 checked out `94719e3d4a3987a306ab5dcd7feafa6547259f0d` (811 tests), and hybrid checked out `4e15d16f433ab147e319b6a91f674cf1f7fca799` (811 tests). #469 added auth tests between baseline and native. Baseline to hybrid includes 91 changed source/test files, so these timings cannot isolate executor effects. The native observations are the initial run plus two workflow reruns. No matched-code speedup is claimed.

Native used the same Make commands, Release configuration and eight-CPU .NET budget. NuGet lookups missed before merge; packages restored directly. Slow failures remain non-blocking and are reported separately below.

| Job | [Docker](https://github.com/TerraScale/terrascale/actions/runs/40064419736) | [Native 1](https://github.com/TerraScale/terrascale/actions/runs/40065733922/attempts/1) | [Native 2](https://github.com/TerraScale/terrascale/actions/runs/40065733922/attempts/2) | [Native 3](https://github.com/TerraScale/terrascale/actions/runs/40065733922/attempts/3) | Native median | Difference (confounded) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| libs | 184 | 81 | 117 | 98 | 98 | -86 |
| auth Fast | 861 | 976 | 784 | 984 | 976 | +115 |
| TerraBase Fast | 1076 | 820 | 996 | 1080 | 996 | -80 |
| auth Slow | 285 | 254 | 212 | 248 | 248 | -37 |
| TerraBase Slow | 400 | 545 | 471 | 521 | 521 | +121 |
| Workflow wall | 18m31s | 16m53s | 17m13s | 18m36s | 17m13s | -1m18s |

All-native was rejected because auth Fast and TerraBase Slow consistently regressed. The final workflow selects native libs/TerraBase Fast, Docker auth Fast/both Slow. The cold hybrid result is below; warm validation will use the next two ordinary PR runs after main seeds caches.

Native auth Slow passed all three attempts. Native TerraBase Slow reported the same five failures in all three attempts: tracked in #543 and #549–552. A green workflow/job does not mean these non-blocking tests passed.

## Historical context

| PR run | Workflow wall | TerraBase Docker step | auth Docker step |
| --- | ---: | ---: | ---: |
| [40016172025](https://github.com/TerraScale/terrascale/actions/runs/40016172025) | 29m39s | 1107 | 1048 |
| [40015347400](https://github.com/TerraScale/terrascale/actions/runs/40015347400) | 28m18s | 1028 | — |
| [40013958709](https://github.com/TerraScale/terrascale/actions/runs/40013958709) | 33m46s | 1195 | 994 |
| [40021492696](https://github.com/TerraScale/terrascale/actions/runs/40021492696) | 24m20s | 968 | 615 |

Code, cache state and scheduling differ across all these comparisons; do not infer a controlled speedup. Main baseline [40011832865](https://github.com/TerraScale/terrascale/actions/runs/40011832865) took 42m20s, including a 1107s production image step.

## Cache audit and decision

Four base layers hit in the sampled historical PR Fast jobs. Restore manifest COPY and NuGet restore also hit in sampled Fast jobs, but materializing the 1.75 GB restore blob took 76–504s; a direct Docker restore miss took 74.6s. In the fresh Docker run, cached ancestors materialized in 56.5s (TerraBase Fast), 83.1s (auth Fast), and 63.9s (TerraBase Slow). Direct native restore took about 16–20s.

Original GitHub cache usage was 10,367,601,119 bytes, close to the 10 GiB limit. Production `mode=max` scopes exported changing source and publish blobs but no checked-build stages. Checked builds also depend on the exact event revision, so exporting them alone would not make the next revision hit.

The final policy exports only Docker `base` to one main-written `mode=min,scope=ci-base` cache. Docker restores NuGet directly. Native jobs read an exact lockfile/SDK-keyed package cache; main alone saves it using actions/cache's zstd archive. Test outputs and results are never cached. BuildKit's GHA exporter does not honor a requested zstd compression option, so no ineffective compression flag is set. Post-merge cache hits must be verified from logs, not inferred from successful steps.

## Cold hybrid and auth audit

| Job | Fresh Docker | [Cold hybrid 40070853511](https://github.com/TerraScale/terrascale/actions/runs/40070853511) | Difference (confounded) |
| --- | ---: | ---: | ---: |
| libs | 184 | 104 | -80 |
| auth Fast | 861 | 1028 | +167 |
| TerraBase Fast | 1076 | 1077 | +1 |
| auth Slow | 285 | 326 | +41 |
| TerraBase Slow | 400 | 453 | +53 |
| Workflow wall | 18m31s | 19m30s | +59s |

Initial scheduling wait grew from 3s to 63s. Auth base setup plus direct restore took 97.8s versus 83.1s cached materialization (+14.7s); checked build grew 129.1→175.2s; tests/audit grew 612.1→715.0s. Actual auth suite duration was 9m46s/797 tests before, 11m23s/811 tests in hybrid, and 11m27s/811 tests in native attempt 1. Dockerfile and Makefile commands are unchanged. Added tests/application changes and runner variance confound the remainder; it is not mostly cache overhead.

Required `test` passed while TerraBase Slow was red with the same five tracked failures. The reporter now runs with `always()`, files/updates main issues, and fails the Slow job with the suite exit code. Slow is outside required `test` dependencies. Main cache population and two subsequent ordinary PR observations will determine whether executor/cache changes are retained; no warm success is claimed yet.
