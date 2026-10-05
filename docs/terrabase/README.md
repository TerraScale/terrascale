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

Every push to `main` that passes CI publishes the tested binaries as the prerelease
`ci-<sha>-run-<run id>` (assets `terrabase-linux-amd64` and `terrabase-linux-arm64`, each with
its `.sha256`). To cut a versioned release:

1. Merge a pull request that sets `<Version>` in
   `src/TerraScale.TerraBase/TerraScale.TerraBase.csproj` to the new version, e.g. `0.2.0`.
2. Tag that commit on `main` and push the tag:

   ```sh
   git fetch origin
   git tag -a v0.2.0 origin/main -m "TerraBase 0.2.0"
   git push origin v0.2.0
   ```

3. `Fast integration` runs on the tag; once `fast` and `release` pass, `publish` creates the
   release `v0.2.0` with both binaries, their SHA-256 files and notes generated from the `main` history
   since the previous `v*` tag. A tag that does not match `<Version>` or is not on `main`
   fails the `release` job and publishes nothing.

Verify a download with `sha256sum -c terrabase-linux-amd64.sha256` (or
`terrabase-linux-arm64.sha256`) in the directory that holds the binary. See `docs/ci.md` for the
pipeline.

## Configuration

Every setting is `TERRABASE_<NAME>` (dashes become underscores) or the equivalent `--name=value`
argument; an argument wins over the environment, and an empty value (`--name=`) clears a setting.
An unknown or invalid setting stops the process with exit code `2`; the error names the setting,
never its value. Port `0` picks a free port.

| Setting | Default | Meaning |
| --- | --- | --- |
| `--region` / `TERRABASE_REGION` | `development` in Development; required otherwise | Node region ID matching Atlas discovery. Lowercase ASCII letter followed by lowercase letters, digits or hyphens; at most 63 characters, no trailing hyphen |
| `--admin-port` / `TERRABASE_ADMIN_PORT` | `9464` | Loopback administrative listener (`/healthz`, `/readyz`, `/metrics`, OpenAPI). 0 to 65535 |
| `--node-name` / `TERRABASE_NODE_NAME` | Machine name | Descriptive node identity in pressure telemetry |
| `--memory-budget` / `TERRABASE_MEMORY_BUDGET` | One quarter of runtime-available memory, at least 1 MiB | Pressure denominator in bytes; not yet an enforced admission limit |
| `--data-directory` / `TERRABASE_DATA_DIRECTORY` | `data` (relative to the working directory) | Directory holding one SQLite file per database. Must be a valid path |
| `--client-port` / `TERRABASE_CLIENT_PORT` | unset (listener off) | Client-facing HTTPS listener, 0 to 65535. Requires `client-certificate` and `client-private-key` |
| `--client-certificate` / `TERRABASE_CLIENT_CERTIFICATE` | unset | PEM certificate file of the client listener. Requires `client-port` and `client-private-key` |
| `--client-private-key` / `TERRABASE_CLIENT_PRIVATE_KEY` | unset | PEM private key file of `client-certificate`. Requires `client-port` and `client-certificate` |
| `--auth-issuer` / `TERRABASE_AUTH_ISSUER` | unset (Management API answers `401`) | Issuer (`iss`) of the ts-auth access tokens that authenticate Management API users, e.g. `https://auth.terrascale.tech/terrascale`. Absolute `https` URL without whitespace, user information, query or fragment; compared with `iss` exactly. Requires `auth-audience` and `client-port`. See `docs/management-api.md` |
| `--auth-audience` / `TERRABASE_AUTH_AUDIENCE` | unset | Comma-separated accepted audiences (`aud`); a token must name at least one. Each is non-empty, without control characters. Requires `auth-issuer` |
| `--management-credential-file` / `TERRABASE_MANAGEMENT_CREDENTIAL_FILE` | unset (credential off) | File holding the management service credential: at least 32 characters, surrounding whitespace ignored, only its SHA-256 is kept. Accepted on the loopback admin listener only, with `X-TerraBase-User-Id` naming the acting user, for bootstrap before ts-auth exists. See `docs/management-api.md` |
| `--management-legacy-owner` / `TERRABASE_MANAGEMENT_LEGACY_OWNER` | unset | ts-auth user id that becomes the owner of organizations from a version 1 catalog when it is migrated. Non-blank, at most 256 characters, no control characters. See `docs/management-api.md` |
| `--peer-port` / `TERRABASE_PEER_PORT` | unset (listener off) | Inter-node gRPC listener (mutual TLS), 0 to 65535. Requires `peer-certificate`, `peer-private-key` and `peer-client-ca` |
| `--peer-certificate` / `TERRABASE_PEER_CERTIFICATE` | unset | PEM certificate file of the peer listener. Requires `peer-port` and the other peer TLS settings |
| `--peer-private-key` / `TERRABASE_PEER_PRIVATE_KEY` | unset | PEM private key file of `peer-certificate`. Requires `peer-port` and the other peer TLS settings |
| `--peer-client-ca` / `TERRABASE_PEER_CLIENT_CA` | unset | PEM CA certificate that every connecting peer's client certificate must chain to. Requires `peer-port` and the other peer TLS settings |
| `--shutdown-grace-seconds` / `TERRABASE_SHUTDOWN_GRACE_SECONDS` | `10` | Seconds shutdown waits for transactions to return their database connections (0 to 3600). It never closes them; the data directory stays owned until they do or the process exits |
| `--readiness-cache-seconds` / `TERRABASE_READINESS_CACHE_SECONDS` | `1` | Seconds completed readiness filesystem results are cached (positive integer, 1 to 60). Zero is prohibited to bound disk work from anonymous traffic. Shutdown bypasses the cache |

### Health probes

Both probes are unauthenticated, return no tenant data, and are served on the admin listener and,
when `client-port` is set, on the client TLS listener.

- `GET /healthz`: liveness, `200 {"status":"ok","version":"<version>","region":"<region>"}`.
- `GET /readyz`: readiness. `200 {"status":"ready","version":"<version>","region":"<region>"}` only when the storage
  engine is open, the data directory is writable, ts-auth's signing keys have been fetched once (only when
  `auth-issuer` is set) and the server is not shutting down; otherwise
  `503 {"status":"unavailable","version":"<version>","region":"<region>"}`. Writable means it can create and remove an
  empty probe file there, so a disk with free inodes but no free blocks still reads as writable.
  Completed filesystem results are cached for one second by default, configurable with `readiness-cache-seconds`,
  to bound disk work from anonymous traffic. Concurrent callers await the same check; they never receive a stale
  cached result while it runs. A running check becomes unavailable after two seconds. Shutdown immediately
  returns 503 regardless of cached or in-flight results; engine closure is checked on every request.

Point load balancers and reverse proxies (Caddy) at the client listener's `/readyz`.

## Region identity

Every client response includes `X-TerraBase-Region`. A request with an empty,
duplicate, or mismatched `X-TerraScale-Region-Pin` receives `421 Misdirected Request`
with the node's region header; TerraBase does not forward it. `/healthz` and
management database responses include `region`, identifying the responding node.

Clients resolve endpoints through Atlas, support an explicit static endpoint
override, and pin a session to the selected region. Keep regional endpoints out
of client code. See [the region contract](docs/region-identity.md) for pinning
and Atlas compatibility.

## Node pressure

`GET /v1/node/pressure` and pressure metrics are available only on the loopback
administrative listener. Measurements are cached; I/O-pool and replication
signals stay unknown, keeping autoscaling gated until #13 and #11 are integrated.
See [signal definitions and follow-ups](docs/node-pressure.md).

## Layout

Feature-sliced: `src/TerraScale.TerraBase/Features/<Feature>/` holds everything
a slice needs. See `AGENTS.md` for the full structure and rules.
