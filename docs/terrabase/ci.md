# CI and releases

Forgejo Actions on the `terrascale-builder` runner (ts-builder image, .NET SDK 11 RC1).

| Workflow | When | Jobs | Blocks merges/releases |
| --- | --- | --- | --- |
| `fast.yml` | push to `main`, pull requests, manual | `fast` and `release` in parallel, then `publish` (main only) | yes |
| `slow.yml` | push to `main` (beside `fast.yml`), nightly 03:17 UTC, manual | `slow` | never |

- **fast**: `make restore` (locked mode), `make fmt-check`, `make build CONFIGURATION=Release`,
  `make test-fast CONFIGURATION=Release`.
- **release**: `scripts/ci/stage-release.sh` runs `make publish`, then smoke-runs the staged
  bytes: `/healthz` must report the csproj version, and an unknown setting must exit with 2.
  On main pushes, `scripts/ci/retain-release.sh` uploads `terrabase-linux-amd64` to the
  prerelease `ci-<sha>-run-<run id>` and reads it back.
- **publish**: runs only after `fast` and `release` both pass. Reads the retained bytes back
  against the release job's manifest and uploads `qualification.json`. A release without
  `qualification.json` is incomplete and must not be deployed.
- **slow**: `make test-slow CONFIGURATION=Release`. A failure opens the issue "Slow test suite
  failed", or comments on it if it is already open (`scripts/ci/report-slow-failure.sh`).

Branch protection on `main` should require `Fast integration / fast` and
`Fast integration / release`.

## Speed

- `fast` and `release` run in parallel; `publish` only promotes bytes and never rebuilds.
- NuGet packages persist in `/cache/nuget/terrabase` on the runner (`scripts/ci/nuget-cache.sh`).
- `release` publishes only the server project; tests are built in `fast` alone.

## Requirements

- Secret `ORG_READ_TOKEN`: read access to the private `TerraScale/ts-libs`, checked out at
  `TS_LIBS_REV`, and to this repository's release attachments for the read-back checks.
- `TS_LIBS_REV` in both workflows pins `ts-libs`; bump them together.
