# TerraBase CI

`.github/workflows/ci.yml` (`CI`) is the only pull-request workflow. Its last job, `all-green`, is the one required
check: it fails when any job it needs failed or was cancelled, and a skipped job passes. No job runs a test except
`journey`.

## Pull requests

- `changes` picks the jobs from the changed paths (`AGENTS.md` and `CLAUDE.md` files select none).
- `checks` runs the policy checks: the legacy-name scan (fail mode), actionlint with shellcheck on the workflows,
  merged EF Core migrations stay unchanged while their project exists (`scripts/ci/migrations-frozen.py`), the
  storage train (`scripts/terrabase/storage-format-check.sh`), the retired-hosting-provider grep, and a rule that fails
  when the pull request adds a test file outside `e2e/` (a path in a `test/`, `tests/` or `*.Test(s)/` folder, or a
  file named `*_test.go`, `*.test.{js,cjs,mjs,ts,tsx}`, `test_*.py` or `*Test(s).cs`).
- The storage train reads the pull request's labels when `checks` runs: after you add or remove a storage-format label,
  re-run `checks` (a label change starts no new run).
- `libs`, `auth` and `terrabase` restore locked, verify the pinned native libraries, check whitespace format and build
  their solution filter in Release with analyzers and warnings as errors, then check that no project fell back to
  Debug (`scripts/ci/release-only.sh`). `libs` also checks code style; `auth` also runs the NuGet audit and the OpenAPI
  contract check (`make auth-check`). Locally: `make terrabase-build`.
- `dashboard` (`dashboard-ci.yml`) builds the dashboard solution in Release and runs its lint set.
- `sdk` runs `make sdk-<sdk>-check` for each changed SDK: a locked install or restore and a build or type check.
- `docs`, `landing` and `status` build the three sites.
- `journey` (`journey.yml`) builds TerraBase, ts-auth and the dashboard from the commit and runs the journey in `e2e/`
  against them, unless every changed file is Markdown or under `docs/`, `docs-site/`, `landing-site/` (other than
  `landing-site/static/pricing.json`) or `status-site/`.
- `test` passes only when `changes`, `checks` and every selected leg (`libs`, `auth`, `terrabase`, `dashboard`,
  `sdk`) passed. It runs when a leg or a release is selected or when `changes` or `checks` failed.

## Pushes to main

A branch need not be up to date with `main` before it merges, so a push runs the legs of its changed areas again on
the merged commit: `changes` selects `libs`, `auth`, `terrabase` and `sdk` as on a pull request, and a change to
TerraBase's server (`src/TerraBase`, its contracts, `src/vendor`) also selects `auth`. `checks` runs the legacy-name
scan; `dashboard`, the sites and `journey` run on pull requests only. `test` passes when `changes`, `checks` and every
selected leg passed. `main` publishes TerraBase once `test` and `terrabase` passed; `main-auth` (or
`main-auth-ordered`, after `main`, when one push changes both TerraBase's server and what ts-auth calls it with)
publishes and releases ts-auth once `test` and `auth` passed. Each publishes from its own Release build
(`scripts/ci/image-payloads.sh publish`); a failed leg or build stops its release. TerraBase reaches the regions only
through a `release-only` dispatch. A `workflow_dispatch` on `main` selects all three .NET legs and both services
(`build-only`: the `terrabase` leg and `main`).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
