# GitHub-hosted CI executor comparison

All durations below are seconds unless formatted as minutes. Job time includes setup and teardown; workflow wall clock includes scheduling. Every run used GitHub-hosted runners. No suites or checks were removed. These are CI observations, not controlled hardware benchmarks.

## Matched application source

The Docker baseline used `b4200204809307e56581bf51bc0aece6b3eecf8c`; the native experiment used `06dc434c48ff07785872784116454c9acb48a1bf`. Both have application source from `847dd312c`. Native used the same Make commands, Release configuration and eight-CPU .NET budget. NuGet cache lookups missed in these pre-merge native runs; packages restored directly. Slow failures remain non-blocking and are reported separately below.

| Job | [Docker](https://github.com/TerraScale/terrascale/actions/runs/40064419736) | [Native 1](https://github.com/TerraScale/terrascale/actions/runs/40065733922/attempts/1) | [Native 2](https://github.com/TerraScale/terrascale/actions/runs/40065733922/attempts/2) | [Native 3](https://github.com/TerraScale/terrascale/actions/runs/40065733922/attempts/3) | Native median | Median difference |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| libs | 184 | 81 | 117 | 98 | 98 | -86 |
| auth Fast | 861 | 976 | 784 | 984 | 976 | +115 |
| TerraBase Fast | 1076 | 820 | 996 | 1080 | 996 | -80 |
| auth Slow | 285 | 254 | 212 | 248 | 248 | -37 |
| TerraBase Slow | 400 | 545 | 471 | 521 | 521 | +121 |
| Workflow wall | 18m31s | 16m53s | 17m13s | 18m36s | 17m13s | -1m18s |

All-native was rejected because auth Fast and TerraBase Slow consistently regressed. The final workflow selects native libs/TerraBase Fast, Docker auth Fast/both Slow. Hybrid validation is recorded in PR #518 after its run finishes.

Native auth Slow passed all three attempts. Native TerraBase Slow reported the same five failures in all three attempts: tracked in #543 and #549–552. A green workflow/job does not mean these non-blocking tests passed.

## Historical context

| PR run | Workflow wall | TerraBase Docker step | auth Docker step |
| --- | ---: | ---: | ---: |
| [40016172025](https://github.com/TerraScale/terrascale/actions/runs/40016172025) | 29m39s | 1107 | 1048 |
| [40015347400](https://github.com/TerraScale/terrascale/actions/runs/40015347400) | 28m18s | 1028 | — |
| [40013958709](https://github.com/TerraScale/terrascale/actions/runs/40013958709) | 33m46s | 1195 | 994 |
| [40021492696](https://github.com/TerraScale/terrascale/actions/runs/40021492696) | 24m20s | 967 | 614 |

Historical code, cache state and scheduling differ; use the fresh matched comparison for acceptance. Main baseline [40011832865](https://github.com/TerraScale/terrascale/actions/runs/40011832865) took 42m20s, including a 1107s production image step.

## Cache audit and decision

Four base layers hit in the sampled historical PR Fast jobs. Restore manifest COPY and NuGet restore also hit in sampled Fast jobs, but materializing the 1.75 GB restore blob took 76–504s; a direct Docker restore miss took 74.6s. In the fresh matched Docker run, cached ancestors materialized in 56.5s (TerraBase Fast), 83.1s (auth Fast), and 63.9s (TerraBase Slow). Direct native restore took about 16–20s.

Original GitHub cache usage was 10,367,601,119 bytes, close to the 10 GiB limit. Production `mode=max` scopes exported changing source and publish blobs but no checked-build stages. Checked builds also depend on the exact event revision, so exporting them alone would not make the next revision hit.

The final policy exports only Docker `base` to one main-written `mode=min,scope=ci-base` cache. Docker restores NuGet directly. Native jobs read an exact lockfile/SDK-keyed package cache; main alone saves it using actions/cache's zstd archive. Test outputs and results are never cached. BuildKit's GHA exporter does not honor a requested zstd compression option, so no ineffective compression flag is set. Post-merge cache hits must be verified from logs, not inferred from successful steps.
