# TerraBase

TerraBase is a leaderless, multi-region document database focused on fast and
efficient synchronization and replication. This repository is the C#/.NET 11
implementation; product goals and contracts are in `../terrabase-rust`
(`GOAL.md`, `docs/`).

## Requirements

- .NET SDK pinned in `global.json`
- `../ts-libs` checked out next to this repository (or pass
  `-p:TerraScaleRoot=<dir containing ts-libs>`)

## Usage

```sh
nice -n 19 make build       # build with analyzers as errors
nice -n 19 make test-fast   # blocking integration suite
nice -n 19 make test-slow   # non-blocking integration suite
nice -n 19 make check       # everything required before a push
nice -n 19 make publish     # native binary in artifacts/publish/terrabase
```

## Configuration

Every setting is `TERRABASE_<NAME>` or `--name=value`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `admin-port` | `9464` | Loopback administrative listener (`/healthz`, `/metrics`, OpenAPI) |

## Layout

Feature-sliced: `src/TerraScale.TerraBase/Features/<Feature>/` holds everything
a slice needs. See `AGENTS.md` for the full structure and rules.
