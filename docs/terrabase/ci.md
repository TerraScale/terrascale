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
- Deploys (push to main, job `deploy`, one at a time in push order): it builds both images at once. Before the rollout, `scripts/fly/fast-gate.sh` requires a passed `test` for the exact tree: the merged pull request's head when the squash merge kept its tree (rebase before merging: deploys start ~1 min after the merge), otherwise main's own `test` (~6 min). The Dockerfile's `terrabase` and `ts-auth` stages are the self-contained release executables on `runtime-deps:11.0-resolute-chiseled` (no shell), pushed to `registry.fly.io/<app>:<sha>`. `scripts/fly/deploy.sh` rolls each app (`ts-terrabase`, then `ts-auth`; config in `src/TerraBase/fly.toml` and `src/TerraScale.Authentication/fly.toml`) to gru, sjc and fra in that order, checking `/healthz` and `/backupz` on each region's new machine (`fly-force-instance-id`) and the commit it runs, and writes each region's time to the job summary. A failed check stops the rollout. For TerraBase a storage-format gate runs first (see below). Secrets: one Fly deploy token per app (`FLY_TOKEN_TS_TERRABASE`, `FLY_TOKEN_TS_AUTH`).
- PR previews (job `preview`, also without waiting for `test`): `scripts/fly/preview.sh` runs both images as one multi-container machine in app `ts-pr-<n>` (org `terrascale-previews`, token `FLY_TOKEN_PREVIEWS`) and comments `https://ts-pr-<n>.fly.dev` on the PR. `previews.yml` deletes it when the PR closes and reaps previews older than 24 hours every hour.
- Other packages are not published automatically. Build an artifact you need with its `make` target, for example `make auth-publish`.

## Storage format and the storage train

TerraBase never migrates a data directory: a build refuses a directory written by another storage format. The format is
`StorageFormat.Id` (`src/TerraBase/Engine/Sqlite/StorageFormat.cs`): the hand-bumped `StorageFormat.Version`, then the
first 12 hex digits of the schema digest. A directory records it in its lock file on create; `/healthz`, `/readyz` and
`/backupz` report it as `storageFormat`, and `terrabase --storage-format` prints the format an image writes.

- Rollout gate: before switching a region, `scripts/fly/deploy.sh` compares the format the region's machine serves (its
  `/healthz`, pinned with `fly-force-instance-id`, answered by that region) with the new image's. Equal: normal rollout.
  Different: `scripts/fly/volume-reset.sh --image` resets the region as one planned step (stop, snapshot and wait for it,
  switch the stopped machine to the new image, clone it onto a fresh volume, check the clone, destroy the old machine,
  delete the old volume), then the normal rollout and live checks run. A machine that does not answer rolls out normally;
  if it then fails, its logs are checked for the refusal (reset) and anything else stops the rollout. Resets only run
  while `ops/fly/greenfield-no-customer-data` exists; each one is listed in the deploy job summary with its snapshot ID.
- Formats: 2 was SQLite (Microsoft.Data.Sqlite); 3 is Turso (T1). A format-3 build refuses a format-2 data directory at
  start with `data directory has storage format 2-…; this build writes 3-…`; there is no conversion.
- Storage train: a pull request that changes what is on disk bumps `StorageFormat.Version` and carries the label
  `storage-format`. One that touches a storage file without changing what is on disk carries `storage-format-unchanged`.
  The `test` job enforces this (`scripts/terrabase/storage-format-check.sh`, which also holds the storage file list kept by
  the storage owners); it reads the labels live, so after labelling re-run the failed job (`gh run rerun <run> --failed`).
- When several `storage-format` pull requests are in flight, merge them back to back. Deploy jobs share one concurrency
  group: one runs, and a newer queued deploy replaces the older queued one. Back-to-back merges therefore roll out at most
  twice (the running one, then the newest), so each region is reset at most twice instead of once per pull request.
