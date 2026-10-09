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
   `src/TerraBase/TerraBase.csproj` to the new version, e.g. `0.2.0`.
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
| `--admin-port` / `TERRABASE_ADMIN_PORT` | `9464` | Loopback administrative listener (`/healthz`, `/readyz`, `/metrics`, operator endpoints). 0 to 65535 |
| `--node-name` / `TERRABASE_NODE_NAME` | Machine name | Descriptive node identity in pressure telemetry |
| `--memory-budget` / `TERRABASE_MEMORY_BUDGET` | One quarter of runtime-available memory, at least 1 MiB | Bytes planned for caches and admitted memory reservations; also the pressure denominator. See [`docs/memory.md`](docs/memory.md) for coverage |
| `--data-directory` / `TERRABASE_DATA_DIRECTORY` | `data` (relative to the working directory) | Directory holding one SQLite file per database. Must be a valid path |
| `--client-port` / `TERRABASE_CLIENT_PORT` | unset (listener off) | Client-facing HTTPS listener, 0 to 65535; also serves the [API reference](#api-reference). Requires `client-certificate` and `client-private-key` |
| `--client-certificate` / `TERRABASE_CLIENT_CERTIFICATE` | unset | PEM certificate file of the client listener. Requires `client-port` and `client-private-key` |
| `--client-private-key` / `TERRABASE_CLIENT_PRIVATE_KEY` | unset | PEM private key file of `client-certificate`. Requires `client-port` and `client-certificate` |
| `--auth-issuer` / `TERRABASE_AUTH_ISSUER` | unset (authenticated APIs answer `401`) | Trusted platform issuer (`iss`) of ts-auth project access tokens for all public APIs and legacy Management user tokens, e.g. `https://auth.terrascale.tech/terrascale`. Absolute `https` URL without whitespace, user information, query or fragment; compared with `iss` exactly. Requires `auth-audience` and `client-port`. See `docs/management-api.md` |
| `--auth-audience` / `TERRABASE_AUTH_AUDIENCE` | unset | Comma-separated accepted audiences (`aud`); a token must name at least one. Each is non-empty, without control characters. Requires `auth-issuer` |
| `--management-credential-file` / `TERRABASE_MANAGEMENT_CREDENTIAL_FILE` | unset (credential off) | File holding the management service credential: at least 32 characters, surrounding whitespace ignored, only its SHA-256 is kept. Accepted on the loopback admin listener only, with `X-TerraBase-User-Id` naming the acting user, for bootstrap before ts-auth exists. See `docs/management-api.md` |
| `--management-legacy-owner` / `TERRABASE_MANAGEMENT_LEGACY_OWNER` | unset | ts-auth user id that becomes the owner of organizations from a version 1 catalog when it is migrated. Non-blank, at most 256 characters, no control characters. See `docs/management-api.md` |
| `--peer-port` / `TERRABASE_PEER_PORT` | unset (listener off) | Inter-node gRPC listener (mutual TLS), 0 to 65535. Requires `peer-certificate`, `peer-private-key` and `peer-client-ca` |
| `--peer-certificate` / `TERRABASE_PEER_CERTIFICATE` | unset | PEM certificate file of the peer listener. Requires `peer-port` and the other peer TLS settings |
| `--peer-private-key` / `TERRABASE_PEER_PRIVATE_KEY` | unset | PEM private key file of `peer-certificate`. Requires `peer-port` and the other peer TLS settings |
| `--peer-client-ca` / `TERRABASE_PEER_CLIENT_CA` | unset | PEM CA bundle that every peer certificate must chain to, in both directions. Requires `peer-port` and the other peer TLS settings |
| `--peer-addresses` / `TERRABASE_PEER_ADDRESSES` | unset | Comma-separated `https://host:port` peers to send changes to; requires `peer-port` |
| `--peers` / `TERRABASE_PEERS` | unset | Every member as comma-separated `name=host:port` peer listeners, this node included under its `node-name`; the others become `peer-addresses` (set one or the other). Names are lowercase letters, digits and inner hyphens, at most 63 characters, distinct, as are the addresses. Requires `peer-port` |
| `--catalog-primary` / `TERRABASE_CATALOG_PRIMARY` | unset (this node is a catalog primary) | The catalog primary this node replicates the management catalog and API keys from, as a member name of `peers` or an `https://host:port` peer listener; it then writes no catalog itself. Naming this node makes it the primary, so one value serves every member. Requires `peer-port`. See `docs/replication.md#catalog` |
| `--write-wait-ms` / `TERRABASE_WRITE_WAIT_MS` | `2000` | Milliseconds a client write waits, after its commit, for every active peer to durably acknowledge it before it answers anyway (0 to 60000; 0 does not wait). See `docs/decisions/0010-write-wait-and-synced-durable-ack.md` |
| `--shutdown-grace-seconds` / `TERRABASE_SHUTDOWN_GRACE_SECONDS` | `10` | Seconds shutdown waits for transactions to return their database connections (0 to 3600). It never closes them; the data directory stays owned until they do or the process exits |
| `--query-budget-seconds` / `TERRABASE_QUERY_BUDGET_SECONDS` | `5` | Seconds of SQLite execution one query may spend before it answers `query too expensive` (1 to 600). It counts wall time while SQLite runs, excluding queueing and the time a consumer spends between rows; the 5 million VM instruction half of the budget is fixed |
| `--readiness-cache-seconds` / `TERRABASE_READINESS_CACHE_SECONDS` | `1` | Seconds completed readiness filesystem results are cached (positive integer, 1 to 60). Zero is prohibited to bound disk work from anonymous traffic. Shutdown bypasses the cache |
| `--backup-*` / `TERRABASE_BACKUP_*` | unset (backup off) | Continuous backup to an S3-compatible bucket (OCI Object Storage) with point-in-time restore: `backup-endpoint`, `backup-region`, `backup-bucket`, `backup-access-key-id`, `backup-secret-key-file`, `backup-kms-endpoint`, `backup-kms-key-id`, `backup-kms-api-key` and `backup-kms-private-key-file`, all or none; optional `backup-retention-days` (`hobby=1,pro=7,enterprise=30`) and `backup-key-rotation-days` (`90`). See [`docs/backup.md`](docs/backup.md) |
| `--nats-*` / `TERRABASE_NATS_*` | unset | ts-orchestrator's NATS, which holds the backup leases (one shipper per file, with automatic takeover): `nats-url`, `nats-credentials-file` and, for mutual TLS, `nats-certificate`, `nats-private-key` and `nats-ca`. See [`docs/backup.md`](docs/backup.md#leases) |
| `--wan-regions` / `TERRABASE_WAN_REGIONS` | unset (no cross-region replication) | Every region of the replication group as comma-separated `region=cell` entries, this node's `region` included, where `cell` is the region's token on ts-orchestrator's NATS fabric (`gru-1=gru,sfo-1=sfo`). Requires `nats-url` and a `node-name` of lowercase letters, digits and inner hyphens. See [`docs/cross-region.md`](docs/cross-region.md) |
| `storage-threads` | processor count | Dedicated threads that run SQLite reads. Each database's writes run on its own write thread, started on demand and stopped after 10 idle seconds, and commit in batches (group commit) |
| `storage-queue` | `1024` | Storage transactions admitted at once; more get `503` with `Retry-After` |

### Health probes

Both probes are unauthenticated, return no tenant data, and are served on the admin listener and,
when `client-port` is set, on the client TLS listener.

- `GET /healthz`: liveness, `200 {"status":"ok","version":"<version>","region":"<region>"}`.
- `GET /readyz`: readiness. `200 {"status":"ready","version":"<version>","region":"<region>"}` only when the storage
  engine is open, the data directory is writable, ts-auth's signing keys have been fetched once (only when
  `auth-issuer` is set), a catalog follower has applied its primary's head once and is not behind a head its primary
  reported (only when `catalog-primary` is set) and the server is not shutting down; otherwise
  `503 {"status":"unavailable","version":"<version>","region":"<region>"}`. Writable means it can create and remove an
  empty probe file there, so a disk with free inodes but no free blocks still reads as writable.
  Completed filesystem results are cached for one second by default, configurable with `readiness-cache-seconds`,
  to bound disk work from anonymous traffic. Concurrent callers await the same check; they never receive a stale
  cached result while it runs. A running check becomes unavailable after two seconds. Shutdown immediately
  returns 503 regardless of cached or in-flight results; engine closure is checked on every request.

Point load balancers and reverse proxies (Caddy) at the client listener's `/readyz`.

Replication setup, certificates, limits and metrics: [`docs/replication.md`](docs/replication.md).
`GET /replication` on the loopback administrative listener reports outbound per-peer
and per-database durable lag, detected journal conflicts and catch-up times; see
[status and alert thresholds](docs/replication.md#replication-status-and-alerts).

## API reference

Public operations follow the project hierarchy in [the route table](docs/api.md). They accept ts-auth project
access JWTs (`project_id` and scoped permissions), validated against the configured platform issuer's JWKS.
Customer application tokens from project tenant issuers do not authenticate TerraScale APIs. Existing
organization routes and `tb_` document keys remain hidden compatibility paths until COS confirms migration;
this release preserves existing catalog ids, data files and replication identities.

The client listener serves the public API reference without credentials:

- `GET /` redirects (`302`, relative `Location: scalar/`) to the Scalar reference at `/scalar/`.
- `GET /openapi/v1.json` is the OpenAPI document, generated by ASP.NET Core OpenAPI.

The admin listener serves neither (`404`). See [the API reference](docs/api-reference.md).

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

`GET /admin/node/pressure` and pressure metrics are available only on the loopback
administrative listener. `/v1/node/pressure` remains a hidden compatibility alias. Measurements are cached;
I/O-pool and replication signals stay unknown, keeping autoscaling gated until #13 and #11 are integrated.
See [signal definitions and follow-ups](docs/node-pressure.md).

## Production replication check

`scripts/prod-replication-check` writes to the configured canary database through the API, then exits non-zero when any
member's documents, versions, replication lag or logs disagree; COS runs it on beta every 20 minutes (usage in its header).
Host observations are required: a failed metrics scrape or log read, missing sender gauges, a partial pair of catalog
follower gauges, or invalid metric values fail the check. Custom `--host-exec` adapters must emit `complete metrics` and
`complete logs` only after collecting each observation, along with the process start time and metric samples. `{ip}` is
the host and `{member}` is its full host:port. Empty or duplicate member inventories and skipped host checks are refused
before any canary writes.

The canary file must name the expected `region` (for example, `"region": "gru-1"`). Before writing, the checker
requires the public API and every configured member to report ready in that region. It pins every client request
with `X-TerraScale-Region-Pin` and requires exactly one matching `X-TerraBase-Region` response header, including
error responses. Redirects are not followed, so a redirected observation fails. Missing or wrong-region observations
fail the run.

A pass proves sampled canary convergence on the configured members. It does not establish a complete serving inventory,
an Anna-only release, or 48 continuous clean hours. TS-677 remains open until the legacy replication path is removed,
every serving member in gru-1 and sfo-1 runs the reviewed artifact, and continuous operational evidence covers the soak
without failures, gaps or unverifiable counter resets.

## Layout

Optional declared JSON Schema per collection: [attach, validate, and detach](docs/collection-schemas.md).

Feature-sliced: `src/TerraBase/Features/<Feature>/` holds everything
a slice needs. See `AGENTS.md` for the full structure and rules.
