# CI and releases

`.github/workflows/ci.yml` runs on every pull request, merge-group check and push to main. The repository is public and every job runs on GitHub-hosted `ubuntu-latest`; the old `CI_RUNNER` variable has been removed. Its one required check, `test`, passes when every area job that ran passed. The area jobs run in parallel. Each exports its `build-<area>` layer after whitespace format, Release compilation with analyzers as errors, static checks and service publication, then runs Fast without caching test results. Fast, Slow, preview and production images reuse that compiled layer with identical build arguments.

| Job | Stage | Runs on a pull request when these change |
|---|---|---|
| `libs` | `test-libs` | libs, `src/TerraScale.CLI`, `benchmark/dotnet`, shared files |
| `auth` | `test-auth` | auth (`src/TerraScale.Authentication*`, its tests, `docs/auth`, `scripts/auth`), `src/TerraBase`, libs, shared files |
| `terrabase` | `test-terrabase` | TerraBase (`src/TerraBase*`, its tests, `scripts/terrabase`), libs, shared files |

Shared files are the root build files (`Dockerfile`, `Makefile`, `Directory.*`, `TerraScale.slnx`, `global.json`, `NuGet.config`, `.editorconfig`, `BannedSymbols.txt`) and `ci.yml`; the `changes` job holds the exact filters. A pull request that touches none of them (docs only) runs no area job. Pushes to main, merge-group checks and `workflow_dispatch` run every area. Test hosts retain an eight-CPU .NET and GC heap budget across runner types; production images retain automatic CPU discovery. Only push-to-main area builds write their scoped GitHub Actions caches (`libs`, `auth`, `terrabase`); PRs, merge-group checks, Slow, previews and runtime-image builds restore only. NuGet restore lives in a separate layer keyed by project, lockfile and MSBuild manifest contents, so application-source changes reuse packages while dependency changes run locked restore again. The native E2E job caches NuGet packages using every repository and SDK lockfile. `dotnet tool restore` is used only by local `make dev` (Aspire); CI uses SDK commands and does not install unused development tools. Runtime images copy the already published executable, without another restore or publish. Test stages always execute even when compilation is cached. Newer PR updates cancel superseded PR tests. Main and merge-group runs are never cancelled by newer commits; freshness checks skip superseded deployments before mutation. The newest main must still pass Fast for its exact tree; if it fails, no replacement rollout starts. The `test` job always runs and fails when any area job failed or was cancelled, so it reports on every pull request even when every area was skipped.

- Reproduce it locally with `nice -n 19 make docker-test`, which builds the `test` stage: all three areas in parallel (needs `../ts-dotnet`; ts-auth's tests start the TerraBase built from the same tree). CI is the gate: no local test/check run is required before pushing or opening a PR (user decision 2026-10-14).
- Fork pull requests receive no SDK, storage or deployment secrets. Because Fast needs the private SDK checkout, their `test` check fails explicitly until the changes run on a branch in this repository; skipped area jobs cannot qualify a fork for merge. Secret-bearing PR jobs run only for branches in this repository.
- Heavy suites (the slow suites, and the 1M-document strict tests and benchmarks once CI runs them) run in independent, non-blocking `slow (auth)` and `slow (terrabase)` jobs (`--target test-slow-auth` / `test-slow-terrabase`, eight-heap test hosts); a failure in one never prevents the other suite from running. They wait for their area builds to become available, including after a Fast failure. Both run only on pushes to main, on `workflow_dispatch`, and on pull requests labelled `full-ci`; `nice -n 19 make test-slow` runs them locally. The combined Docker target `test-slow` exports `auth/{log,exit}` and `terrabase/{log,exit}`. TerraBase Slow requires the bucket-scoped `TIGRIS_CI_CREDENTIALS` repository secret, mounted through BuildKit as `tigris-ci`; missing credentials fail instead of skipping the real-storage test. The durable private bucket is `ts-terrabase-ci` in org `terrascale`. Each invocation prefixes all objects, including `_system` keys, with a fresh ULID and deletes only that prefix after shutdown. No org-level storage-create token reaches CI. For local Slow, load `~/.config/terrascale/tigris-ci.env` privately; for Docker Slow, pass `--secret id=tigris-ci,src=<credential-file>`. Only main opens `slow-failure` issues, including when a build produces no results; every failed test is reported. Adding the label starts a new run (`pull_request` `labeled`); note that adding *any* label re-runs CI and cancels the running check for that pull request.
## Runtime delivery

GitHub-hosted CI publishes images to GHCR. Workload admission and rollout authority
belong to ts-orch; the current OCI production host is moving under its management.
See [hosting status and evidence limits](../oci.md). Historical preview and volume-reset
recipes have been retired; this page does not establish an OCI preview or reset procedure.

## Storage format and the storage train

TerraBase never migrates a data directory: a build refuses a directory written by another storage format. The format is
`StorageFormat.Id` (`src/TerraBase/Engine/Sqlite/StorageFormat.cs`): the hand-bumped `StorageFormat.Version`, then the
first 12 hex digits of the schema digest. A directory records it in its lock file on create; `/healthz`, `/readyz` and
`/backupz` report it as `storageFormat`, and `terrabase --storage-format` prints the format an image writes.
- Formats: 2 was SQLite (Microsoft.Data.Sqlite); 3 is Turso (T1); 4 drops the change feed and history tables (R2c-2). A format-3 build refuses a format-2 data directory at
  start with `data directory has storage format 2-…; this build writes 3-…`; there is no conversion.
- Storage train: a pull request that changes what is on disk bumps `StorageFormat.Version` and carries the label
  `storage-format`. One that touches a storage file without changing what is on disk carries `storage-format-unchanged`.
  The `test` job enforces this (`scripts/terrabase/storage-format-check.sh`, which also holds the storage file list kept by
  the storage owners); it reads the labels live, so after labelling re-run the failed job (`gh run rerun <run> --failed`).
- Batch approved storage changes into one format bump before a coordinated cluster reset. Hold main merges and retries
  during that reset. Deploy jobs share one concurrency group to prevent overlap. A new main push leaves an active rollout running; only the newest exact-tree Fast-qualified main starts a replacement after the shared deploy group is free. The replacement fixes forward through every region with the same storage and live-health gates.
