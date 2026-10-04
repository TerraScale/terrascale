# TerraBase

TerraBase is a leaderless, multi-region document database focused on fast and
efficient synchronization and replication. This repository is the C#/.NET 11
implementation; product goals and contracts are in `../terrabase-rust`
(`GOAL.md`, `docs/`).

## Requirements

- .NET SDK pinned in `global.json`

## Usage

```sh
nice -n 19 make build       # build with analyzers as errors
nice -n 19 make test-fast   # blocking integration suite
nice -n 19 make test-slow   # non-blocking integration suite
nice -n 19 make check       # everything required before a push
nice -n 19 make publish     # native binary in artifacts/publish/terrabase
```

## Releases

Every push to `main` that passes CI publishes the tested binary as the prerelease
`ci-<sha>-run-<run id>` (assets `terrabase-linux-amd64` and its `.sha256`). To cut a
versioned release:

1. Merge a pull request that sets `<Version>` in
   `src/TerraScale.TerraBase/TerraScale.TerraBase.csproj` to the new version, e.g. `0.2.0`.
2. Tag that commit on `main` and push the tag:

   ```sh
   git fetch origin
   git tag -a v0.2.0 origin/main -m "TerraBase 0.2.0"
   git push origin v0.2.0
   ```

3. `Fast integration` runs on the tag; once `fast` and `release` pass, `publish` creates the
   release `v0.2.0` with the binary, its SHA-256 and notes generated from the `main` history
   since the previous `v*` tag. A tag that does not match `<Version>` or is not on `main`
   fails the `release` job and publishes nothing.

Verify a download with `sha256sum -c terrabase-linux-amd64.sha256`. See `docs/ci.md` for the
pipeline.

## Configuration

Every setting is `TERRABASE_<NAME>` or `--name=value`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `admin-port` | `9464` | Loopback administrative listener (`/healthz`, `/metrics`, OpenAPI) |

## Layout

Feature-sliced: `src/TerraScale.TerraBase/Features/<Feature>/` holds everything
a slice needs. See `AGENTS.md` for the full structure and rules.
