# TS-648 actor transport diagnostic

This measures the preparatory logical-actor transport, not database throughput or durability. No core affinity is applied.

UTC: 2026-10-08T16:19:00.9874345+00:00; available processors: 2; runtime: 11.0.0.
TerraBase revision: 614b9eb1f8be4ed43f79573d32ed9772f4214b52 (include working diff when dirty).
Harness revision: 3991be9ff9c9fbd53a3ab9bb478e9c5b48d9ac8b (include working diff and untracked sources when dirty).
Operations/trial: 128; CPU iterations/operation: 20; keys: 64; producers: 32; payload admission: 64 bytes.
Trial deadline including warmup: 60 seconds; disposal deadline: 5 seconds.

| Actors | Zipf exponent | Repeat | ops/s | p99 ms | CPU seconds | Peak RSS MiB | Callback count |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 0 | 1 | 273212.4 | 0.108 | 0.002 | 48.0 | 128 |
| 2 | 0 | 1 | 330407.8 | 0.164 | 0.001 | 49.3 | 128 |
| 1 | 0.99 | 1 | 425390.5 | 0.138 | 0.001 | 49.5 | 128 |
| 2 | 0.99 | 1 | 470934.5 | 0.106 | 0.001 | 49.7 | 128 |
| 1 | 1.2 | 1 | 410125.0 | 0.067 | 0.001 | 49.9 | 128 |
| 2 | 1.2 | 1 | 557734.2 | 0.096 | 0.000 | 50.1 | 128 |

CPU and process-lifetime peak RSS include the load generator; they are not separate server measurements. Repeats run in one process.
A Zipf hot key remains serialized on its owner. Memory replication and the SQLite flusher are not measured.

## Scope and source provenance

Smoke receipt only: all six cases completed with exactly 128 measured callbacks each (768 total), plus
128 warmup callbacks per case. This verifies compatibility with the latest TerraBase slice's opt-in actor
transport. It does not exercise the Anna database engine, SQLite persistence, lattice replication, HTTP,
durability or database scaling. The parent reports a pre-rebase 9/9 actor regression pass; that suite was
not rerun here, and this receipt makes no claim about it.

The run used low CPU priority (`nice -n 19`) and `DOTNET_PROCESSOR_COUNT=2`. This limits the runtime's reported
processor count; it does not pin cores or establish a two-core CPU quota. These short shared-host timings
are not a performance baseline or scaling evidence. CPU and peak RSS include the generator.

Both source checkouts were clean and committed before the smoke run. The harness was measured at
`3991be9ff9c9fbd53a3ab9bb478e9c5b48d9ac8b`; the subsequent receipt-only commit does not change its implementation.
TerraBase remained at `614b9eb1f8be4ed43f79573d32ed9772f4214b52`, with its fetched `origin/main` at
`3b3cc0d9cae6e988179ccd55819717075900a1aa`. No parent source files were edited.

`git rebase origin/main` in the benchmark worktree returned “Current branch cos/ts-648-actor-harness is up to date.”
The pre/post-rebase HEAD was `6db0257591eddeba8e33d2379baadd954ffec3fd`; fetched benchmark `origin/main` was
`15c6aef2e3e4855bcfdc1c7f3f6d29d1c2212230`. There were no conflicts or rewritten commits. Existing harness
targets, `load-check`, `.forgejo` workflows, load-test and HTTPS tooling were preserved.

## Validation

`actor-check` passed after the reviewer fixes: both Release builds had zero warnings/errors, both C# whitespace
checks passed, and all six process regression checks passed. Those checks cover relative root resolution in
both Make targets, each Git lookup failing closed, empty revision fallback and receipt preservation,
invalid deadline rejection, and a stalled real router consumer with stalled disposal producing a `FAILED`
receipt and exiting within the regression runner's 20-second ceiling. An initial regression attempt supplied
the deadline option twice; the check was corrected before the successful final run.

The final smoke command exited 0. `git diff --check` passed. Review found no remaining findings within this
transport diagnostic's scope. The broader HTTPS/load-test lanes were not rerun; their sources and CI were unchanged.

Commands executed from `/home/mariogk/.t3/worktrees/ts-benchmark/cos-ts-648-actor-harness`:

```sh
git rebase origin/main
nice -n 19 env DOTNET_PROCESSOR_COUNT=2 make actor-check \
  DOTNET=/tmp/ts648-harness-dotnet \
  ACTOR_PYTHON=/home/mariogk/.local/share/mise/installs/python/3.14.8/bin/python3 \
  TERRABASE_ROOT=/home/mariogk/.t3/worktrees/terrabase/cos-ts-648-full-anna-execution-per-core
nice -n 19 env DOTNET_PROCESSOR_COUNT=2 make actor-run \
  DOTNET=/tmp/ts648-harness-dotnet \
  TERRABASE_ROOT=/home/mariogk/.t3/worktrees/terrabase/cos-ts-648-full-anna-execution-per-core \
  ACTOR_ARGS='--operations=128 --iterations=20 --repeats=1 --keys=64 --actors=1,2 --output=../bench/ts648-preparation/receipt20261008-rebased.md'
git diff --check
```

The .NET wrapper supplies `-maxcpucount:2 -nodeReuse:false -p:UseSharedCompilation=false` to builds and
`--no-build` to runs; `actor-check` built the measured implementation first. The Python executable was
selected explicitly because the host's Python launcher tries to resolve this checkout's unrelated missing Rust toolchain.

## Parent pre-push checks

No push or merge was performed. Before pushing, refresh remote refs and confirm the branch still contains
main, the worktree is clean, and the review diff has no whitespace errors:

```sh
git fetch origin
git merge-base --is-ancestor origin/main HEAD
git status --short --branch
git diff --check origin/main...HEAD
```

If either source changes, rerun the `actor-check` command above and the smoke command with a **new** receipt
filename. Never reuse this receipt path. If main advances past this branch, rebase before revalidating.
Parent owns the PR update, remote push and watch; this implementation task did not publish review comments.
