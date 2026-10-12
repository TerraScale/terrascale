# CI and releases

`.github/workflows/ci.yml` runs on every pull request and push to main, on Blacksmith runners. Its one required check, `test`, passes when every area job that ran passed. The area jobs run in parallel, each building one stage of the root `Dockerfile`: whitespace format, the area's Release build with analyzers as errors (`libs.slnf`, `auth.slnf`, `terrabase.slnf`; together the whole solution), the area's checks and its Fast suite.

| Job | Stage | Runner | Runs on a pull request when these change |
|---|---|---|---|
| `libs` | `test-libs` | 4 vCPU | libs, `src/TerraScale.CLI`, `benchmark/dotnet`, shared files |
| `auth` | `test-auth` | 4 vCPU | auth (`src/TerraScale.Authentication*`, its tests, `docs/auth`, `scripts/auth`), libs, shared files |
| `terrabase` | `test-terrabase` | 16 vCPU | TerraBase (`src/TerraBase*`, its tests, `scripts/terrabase`), libs, shared files |

Shared files are the root build files (`Dockerfile`, `Makefile`, `Directory.*`, `TerraScale.slnx`, `global.json`, `NuGet.config`, `.editorconfig`, `BannedSymbols.txt`) and `ci.yml`; the `changes` job holds the exact filters. A pull request that touches none of them (docs only) runs no area job. Pushes to main run every area. Each job keeps its own Blacksmith Docker layer cache (`terrascale-<job>`) and NuGet cache mount; a newer push to a pull request cancels its running check.

- Reproduce it locally with `make docker-test`, which builds the `test` stage: all three areas in parallel (needs `../ts-dotnet`; ts-auth's tests start the TerraBase built from the same tree). Before pushing, `make check` runs the same gates plus the benchmark checks.
- The slow suites run on pushes to main in the non-blocking `slow` job (`--target test-slow`); `make test-slow` runs them locally.
- Deploys (push to main, job `deploy`, one at a time in push order): it builds both images at once. Before the rollout, `scripts/fly/fast-gate.sh` requires a passed `test` for the exact tree: the merged pull request's head when the squash merge kept its tree (rebase before merging: deploys start ~1 min after the merge), otherwise main's own `test` (~6 min). The Dockerfile's `terrabase` and `ts-auth` stages are the self-contained release executables on `runtime-deps:11.0-resolute-chiseled` (no shell), pushed to `registry.fly.io/<app>:<sha>`. `scripts/fly/deploy.sh` rolls each app (`ts-terrabase`, then `ts-auth`; config in `src/TerraBase/fly.toml` and `src/TerraScale.Authentication/fly.toml`) to gru, sjc and fra in that order, checking `/healthz` on each region's new machine (`fly-force-instance-id`), and writes each region's time to the job summary. A failed check stops the rollout. Secrets: one Fly deploy token per app (`FLY_TOKEN_TS_TERRABASE`, `FLY_TOKEN_TS_AUTH`).
- PR previews (job `preview`, also without waiting for `test`): `scripts/fly/preview.sh` runs both images as one multi-container machine in app `ts-pr-<n>` (org `terrascale-previews`, token `FLY_TOKEN_PREVIEWS`) and comments `https://ts-pr-<n>.fly.dev` on the PR. `previews.yml` deletes it when the PR closes and reaps previews older than 24 hours every hour.
- Other packages are not published automatically. Build an artifact you need with its `make` target, for example `make auth-publish`.
