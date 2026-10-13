# Three-region replication matrix — CI resource correction, 2026-10-13

Full Release `make check` and the local 16-case matrix passed after rebasing onto main's CI resource correction (#212). This measures loopback convergence on delta; live Fly qualification remains pending T2 deployment and isolated e2e credentials.

- Tested source: `11d4624dfab46854b41eb8c8e8b0a4abb2bcc661`; base: main `745ea0d586782c33d1b6e2a21d8efc20b83f6d5c`.
- SDK: `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`, matching CI's pin; .NET SDK `11.0.100-rc.1.26425.128`.
- Host: delta, Intel i7-13620H, 16 logical CPUs. Release, server GC, `DOTNET_PROCESSOR_COUNT=8`, `DOTNET_GCHeapCount=8`; runtime logs confirm 8 CPUs and 8 heaps. Test processes keep the repository's parallel execution.
- Builds/tests ran under `ts-heavy` / nice 19; other host work contributes to timings. No product, timeout or assertion changes were introduced by this rebase.

## Commands and validation

The gate ran detached through a systemd user service:

```sh
systemd-run --user --unit=cos-replication-resource-gate-20261013 \
  --collect --service-type=exec ~/.local/bin/ts-heavy bash \
  /tmp/cos-replication-matrix-proof/resource-gate.sh
```

The script exports the CPU/heap values above and `TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk`, then runs:

```sh
make check CONFIGURATION=Release TS_DOTNET_DIR="$TsDotnetRoot"
TERRABASE_E2E_RESULTS=/tmp/cos-replication-matrix-proof/resource-gate-matrix-outcomes.jsonl \
  make terrabase-replication-matrix CONFIGURATION=Release
```

| Check | Actual result |
| --- | --- |
| Formatting, build, analyzers, security/OpenAPI and Go checks | Full `make check` passed, exit 0 |
| Endpoints / logging | 88/88 / 46/46 passed |
| Auth Fast / native driver | 664/664 / 6/6 passed |
| TerraBase Fast | 1569/1569 passed; zero failures/skips; 2m 14.557s |
| Replication matrix | 16/16 actual passes; zero failures/skips; 18.097s |

The detached wrapper exited 127 only after both test commands passed because its PATH lacked `actionlint`. The supplemental checks then passed at nice 19 using `/home/mariogk/.local/share/mise/shims/actionlint -shellcheck='' .github/workflows/ci.yml`, `bash scripts/terrabase/e2e-summary-test.sh` (`e2e-summary-test: ok`) and `git diff --check`. The original PATH error is preserved in `resource-gate-actionlint-path-error.log`. Docker execution and Slow were not run locally; CI remains the merge gate.

The prior #208 CI failure on `04afd7b2` ran on alpha-s1 with 44 CPUs/heaps and failed two oversized backup-listing cases during cleanup before the matrix started. Those exact two cases subsequently passed 20 repetitions locally in Release pinned to CPUs 0,2 (40/40, zero failures/skips; 3.550–5.132s per pair). The rebase preserves main's resource correction; no test was weakened.

## Client-observed local convergence

Ten documents per direction, from successful PUT acknowledgement to first matching target read; all three copies are verified. Timings include HTTP, 20 ms polling and parallel host contention. Median averages the two middle samples; nearest-rank p95 is the maximum for ten samples. These are local observations, not WAN latency estimates.

| Direction | Samples | Min (ms) | Median (ms) | p95 (ms) | Max (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| gru → sjc | 10 | 35.9 | 86.3 | 196.1 | 196.1 |
| sjc → gru | 10 | 62.6 | 89.6 | 172.3 | 172.3 |
| gru → fra | 10 | 54.0 | 70.0 | 193.5 | 193.5 |
| fra → gru | 10 | 58.3 | 92.2 | 2186.0 | 2186.0 |
| sjc → fra | 10 | 63.5 | 93.8 | 175.3 | 175.3 |
| fra → sjc | 10 | 49.9 | 92.8 | 2216.5 | 2216.5 |

Each graceful stop/rejoin case verifies survivor writes, replicated tombstones, concurrent conflicting patches and collection counts/digests. No client writes after restart trigger catch-up. Catch-up starts at restart completion and ends when the source's 20 documents are readable on the rejoined node.

| Source → rejoined region | Documents | Catch-up (ms) |
| --- | ---: | ---: |
| gru → sjc | 20 | 2773 |
| sjc → gru | 20 | 7958 |
| gru → fra | 20 | 2730 |
| fra → gru | 20 | 8158 |
| sjc → fra | 20 | 7825 |
| fra → sjc | 20 | 8114 |

## Raw evidence and live gate

Logs/outcomes remain on delta in `/tmp/cos-replication-matrix-proof/`.

| File | SHA-256 |
| --- | --- |
| `resource-gate-make-check.log` | `b7e25a241cabf70cbd1f4f49702b418f419b60663b32a4172be1d5f0ac92835d` |
| `resource-gate-matrix.log` | `43a3971cd6597402493c04b0bdd8c91b8ae8a64ebbb0c4d4032964fa7885c513` |
| `resource-gate-matrix-outcomes.jsonl` | `5587f168e49255b26809bec46ecf10e142126b8a298269a789404cc23b1d91e6` |

PR: https://github.com/TerraScale/terrascale/pull/208. The post-deploy job requires 13 actual Fly passes with `fly-prefer-region` and serving-region verification. Live stop/rejoin runs separately on a disposable app. There is no successful live run URL yet. Live WAN convergence, abrupt power loss, permanent origin loss, rebuild certification and incarnation fencing are not qualified by this local proof.
