# CI and releases

`.github/workflows/ci.yml` runs on every pull request and push to main, on the self-hosted alpha runners. Its one required check, `test`, passes when every area job that ran passed. The area jobs run in parallel, each building one stage of the root `Dockerfile`: whitespace format, the area's Release build with analyzers as errors (`libs.slnf`, `auth.slnf`, `terrabase.slnf`; together the whole solution), the area's checks and its Fast suite.

| Job | Stage | Runs on a pull request when these change |
|---|---|---|
| `libs` | `test-libs` | libs, `src/TerraScale.CLI`, `benchmark/dotnet`, shared files |
| `auth` | `test-auth` | auth (`src/TerraScale.Authentication*`, its tests, `docs/auth`, `scripts/auth`), `src/TerraBase`, libs, shared files |
| `terrabase` | `test-terrabase` | TerraBase (`src/TerraBase*`, its tests, `scripts/terrabase`), libs, shared files |

Shared files are the root build files (`Dockerfile`, `Makefile`, `Directory.*`, `TerraScale.slnx`, `global.json`, `NuGet.config`, `.editorconfig`, `BannedSymbols.txt`) and `ci.yml`; the `changes` job holds the exact filters. A pull request that touches none of them (docs only) runs no area job. Pushes to main and `workflow_dispatch` run every area. Test hosts use eight .NET CPUs and GC heaps even though the shared remote BuildKit sees the host's 44 CPUs; production images retain automatic Fly CPU discovery. Alpha's shared persistent BuildKit reuses layers and cache mounts across jobs. GitHub Actions cache exports are disabled there by the existing `CI_RUNNER` selection. A newer push cancels the previous CI run for the same ref, including obsolete main tests and rollouts. The newest main must still pass Fast for its exact tree; if it fails, no replacement rollout starts. The `test` job always runs and fails when any area job failed or was cancelled, so it reports on every pull request even when every area was skipped.

- Reproduce it locally with `make docker-test`, which builds the `test` stage: all three areas in parallel (needs `../ts-dotnet`; ts-auth's tests start the TerraBase built from the same tree). Before pushing, run the build plus targeted tests; alpha CI is the full-suite gate (user decision 2026-10-13).
- Heavy suites (the slow suites, and the 1M-document strict tests and benchmarks once CI runs them) run in independent, non-blocking `slow (auth)` and `slow (terrabase)` jobs (`--target test-slow-auth` / `test-slow-terrabase`, eight-heap test hosts); a failure in one never prevents the other suite from running. Both run only on pushes to main, on `workflow_dispatch`, and on pull requests labelled `full-ci`; `make test-slow` runs them locally. The combined Docker target `test-slow` exports `auth/{log,exit}` and `terrabase/{log,exit}`. TerraBase Slow requires the bucket-scoped `TIGRIS_CI_CREDENTIALS` repository secret, mounted through BuildKit as `tigris-ci`; missing credentials fail instead of skipping the real-storage test. The durable private bucket is `ts-terrabase-ci` in org `terrascale`. Each invocation prefixes all objects, including `_system` keys, with a fresh ULID and deletes only that prefix after shutdown. No org-level storage-create token reaches CI. For local Slow, load `~/.config/terrascale/tigris-ci.env` privately; for Docker Slow, pass `--secret id=tigris-ci,src=<credential-file>`. Only main opens `slow-failure` issues, including when a build produces no results; every failed test is reported. Adding the label starts a new run (`pull_request` `labeled`); note that adding *any* label re-runs CI and cancels the running check for that pull request.
- Deploys (push to main, job `deploy`, only the newest main, one rollout at a time): it builds TerraBase once, rolls it out, then builds ts-auth once and rolls it out. Each region reuses that app’s image. `scripts/fly/current-main.sh` skips a superseded run before image builds and rechecks main after Fast; API failures block deployment. A skipped rollout does not run live E2E. Before the rollout, `scripts/fly/fast-gate.sh` requires a passed `test` for the exact tree: the merged pull request's head when the squash merge kept its tree (rebase before merging: deploys start ~1 min after the merge), otherwise main's own `test` (~6 min). The Dockerfile's `terrabase` and `ts-auth` stages are the self-contained release executables on `runtime-deps:11.0-resolute-chiseled` (no shell), pushed to `registry.fly.io/<app>:<sha>`. `scripts/fly/deploy.sh` rolls each app (`ts-terrabase`, then `ts-auth`; config in `src/TerraBase/fly.toml` and `src/TerraScale.Authentication/fly.toml`) to gru, sjc and fra in that order, checking `/healthz` and `/backupz` on each region's new machine (`fly-force-instance-id`) and the commit it runs, and writes each region's elapsed time and UTC start/end timestamps to the job summary. A failed check stops the rollout. For TerraBase a storage-format gate runs first (see below). Secrets: one Fly deploy token per app (`FLY_TOKEN_TS_TERRABASE`, `FLY_TOKEN_TS_AUTH`).
- PR previews (job `preview`, after `test` passes): `scripts/fly/preview.sh` runs both images as one multi-container machine in app `ts-pr-<n>` (org `terrascale-previews-515`, token `FLY_TOKEN_PREVIEWS`). The shared 1 CPU / 1 GB machine stops when idle and starts on a request; its volume is 1 GB. Each container receives only its own secret mappings. Creation provisions the preview issuer's first signing key through the existing operator command; updates preserve it. Both `/readyz` checks must pass before the workflow comments `https://ts-pr-<n>.fly.dev` on the PR. `previews.yml` deletes it when the PR closes and reaps previews older than 24 hours every hour.
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
- Formats: 2 was SQLite (Microsoft.Data.Sqlite); 3 is Turso (T1); 4 drops the change feed and history tables (R2c-2). A format-3 build refuses a format-2 data directory at
  start with `data directory has storage format 2-…; this build writes 3-…`; there is no conversion.
- Storage train: a pull request that changes what is on disk bumps `StorageFormat.Version` and carries the label
  `storage-format`. One that touches a storage file without changing what is on disk carries `storage-format-unchanged`.
  The `test` job enforces this (`scripts/terrabase/storage-format-check.sh`, which also holds the storage file list kept by
  the storage owners); it reads the labels live, so after labelling re-run the failed job (`gh run rerun <run> --failed`).
- When several `storage-format` pull requests are in flight, merge them back to back. Deploy jobs share one concurrency
  group to prevent overlap. A new main push cancels the superseded workflow, including an active rollout, and only the
  newest exact-tree Fast-qualified main starts the replacement. Already-issued Fly operations can finish during
  cancellation; the replacement fixes forward through every region with the same storage and live-health gates.
