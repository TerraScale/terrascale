# TerraBase CI

`.github/workflows/ci.yml` (`CI`) is the only pull-request workflow. Its last job, `all-green`, is the one required
check: it fails when any job it needs failed or was cancelled, and a skipped job passes. No job runs a test except
`journey`.

## Pull requests

- `changes` picks the jobs from the changed paths, and each filter lists only the files its job reads: from
  `sdk/dotnet`, ts-auth and the dashboard take its build files, `src/` and the embedded
  `contracts/auth/error-codes.json`; from `src/vendor`, `terrabase` takes no Markdown or patch, from `scripts/vendor`
  only the two native-library scripts, and from `benchmark/` only the storage-engine pin file those scripts read.
  `AGENTS.md` and `CLAUDE.md` files select none.
- Every job that builds or lints checks out the merge commit GitHub prepares, as the journey does.
- `checks` runs the policy checks: the legacy-name scan (fail mode), actionlint with shellcheck on the workflows,
  merged EF Core migrations stay unchanged while their project exists (`scripts/ci/migrations-frozen.py`), the
  storage train (`scripts/terrabase/storage-format-check.sh`), the retired-hosting-provider grep, and a rule that fails
  when the pull request adds a file outside `e2e/`, or moves one in, whose folder or name marks it as a test (the
  pattern is in the `No test file outside the journey` step of `ci.yml`).
- The storage train reads the pull request's labels when `checks` runs: after you add or remove a storage-format label,
  re-run `checks` (a label change starts no new run).
- `libs`, `auth` and `terrabase` restore their own solution filter locked and check whitespace format; `terrabase`,
  the only leg that builds the native-library driver, also verifies the pinned native libraries. `libs` and `auth`
  build their solution filter in Release with analyzers and warnings as errors; `libs` also checks code style, `auth`
  also runs the NuGet audit and the OpenAPI contract check (`make auth-check`). `terrabase` compiles once: its build
  is the linux-arm64 single-file release publish (`make terrabase-publish`), which compiles every project of
  `terrabase.slnf` in Release with the same analyzers and fails when a native library lies beside the file. Each leg
  then checks that no project fell back to Debug (`scripts/ci/release-only.sh`). Locally: `make terrabase-build`.
- `dashboard` (`dashboard-ci.yml`) builds the dashboard solution in Release and runs its lint set.
- `sdk` runs `make sdk-<sdk>-check` for each changed SDK: a locked install or restore and a build or type check.
  `sdk-release.yml` and `scripts/sdk` select no SDK: the checks never read them.
- `docs` (`make docs-build`) compares `docs-site/data/canonical/{pricing,regions}.json` with the same commit's
  `landing-site/static/` files, checks its generated pages (the `--check` modes of `generate-pricing.mjs`,
  `generate-llms.mjs` and `i18n-sync.mjs`) and builds the docs site, with no request to production; `landing` and
  `status` build theirs.
- `journey` (`journey.yml`) builds TerraBase, ts-auth and the dashboard from the commit and runs the journey in `e2e/`
  against them, unless every changed file is Markdown or under `docs/`, `docs-site/`, `landing-site/` (other than
  `landing-site/static/pricing.json`) or `status-site/`, or is a file the journey never reads: another workflow,
  anything under `benchmark/` but that storage-engine pin file, anything under `aspire/` or `ops/`, a script its builds
  never run, a solution file, `Dockerfile` or a root dotfile (the `journey` filter of `ci.yml` lists them).
- `test` passes only when `changes`, `checks` and every selected leg (`libs`, `auth`, `terrabase`, `dashboard`,
  `sdk`) passed. It runs when a leg or a release is selected or when `changes` or `checks` failed.

## Pushes to main

A branch need not be up to date with `main` before it merges, so a push runs the legs of its changed areas again on the
merged commit: `changes` selects `libs`, `auth`, `terrabase` and `sdk` as on a pull request. `checks` runs the
legacy-name scan; `dashboard`, the sites and `journey` run on pull requests only. `test` passes when `changes`,
`checks` and every selected leg passed. `main` publishes TerraBase once `test` and `terrabase` passed; `main-auth` (or
`main-auth-ordered`, after `main`, when one push changes both TerraBase's server and what ts-auth calls it with)
publishes and releases ts-auth once `test` and `auth` passed. Each publishes from its own Release build
(`scripts/ci/image-payloads.sh publish`); a failed leg or build stops its release. TerraBase reaches the regions only
through a `release-only` dispatch. Every release runs the newest published ts-orchestrator `ci-` release that carries
`qualification.json` and stops unless the binary's sha256 is the one it lists (`scripts/ci/fetch-ts-orch.sh`). A
`workflow_dispatch` on `main` selects all three .NET legs and both services (`build-only`: the `terrabase` leg and
`main`).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
