# TerraBase

Read the [required zero-configuration operating contract](zero-configuration.md).
Current configuration paths are implementation limitations, not production activation instructions.

Hosting: [OCI and ts-orch](../oci.md); node discovery: [TerraBase on ts-orch](ts-orch.md).
Local processes: [TerraBase local host](local-host.md).
Recovery: [Copy placement and snapshot routing](recovery-copy-placement.md).

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

3. `Fast integration` runs on the tag; once `check` and `release` pass, `publish` creates the
   release `v0.2.0` with both binaries, their SHA-256 files and notes generated from the `main` history
   since the previous `v*` tag. A tag that does not match `<Version>` or is not on `main`
   fails the `release` job and publishes nothing.

Verify a download with `sha256sum -c terrabase-linux-amd64.sha256` (or
`terrabase-linux-arm64.sha256`) in the directory that holds the binary. Releases are not published automatically; see `docs/terrabase/ci.md`.

## Configuration

Production identity, membership, region discovery and backup follow the
[operating contract](zero-configuration.md). OCI Vault delivers runtime credentials;
CI uses GitHub Actions secrets. Limits and timeouts have one correct setting in code.

**Current gap (TS-773):** the runtime still parses `TERRABASE_*`/`--name`
options and guards listeners, authentication, backup and cross-region replication
on their presence. The docs change does not remove those paths or establish
an automatic production bootstrap. Preserve validation and secret-free errors
while replacing them; missing authentication must never grant access.

### Health probes

Both probes are unauthenticated, return no tenant data, and are served on the internal
and public listeners.

- `GET /healthz`: liveness, `200 {"status":"ok","version":"<version>","region":"<region>"}`.
- `GET /health/replication`: the public replication summary for status pages (region codes, `ok`/`degraded`, lag
  seconds; see [api.md](api.md#health)).
- `GET /readyz`: readiness. `200 {"status":"ready","version":"<version>","region":"<region>"}` only when the storage
  engine is open, the data directory is writable, ts-auth's signing keys have been fetched once (only when
  `auth-issuer` is set) and the server is not shutting down; otherwise
  `503 {"status":"unavailable","version":"<version>","region":"<region>"}`. Writable means it can create and remove an
  empty probe file there, so a disk with free inodes but no free blocks still reads as writable.
  Completed filesystem results are cached for one second in the current implementation,
  to bound disk work from anonymous traffic. Concurrent callers await the same check; they never receive a stale
  cached result while it runs. A running check becomes unavailable after two seconds. Shutdown immediately
  returns 503 regardless of cached or in-flight results; engine closure is checked on every request.

Point load balancers and reverse proxies (Caddy) at the client listener's `/readyz`.

Target replication design (Turso + CDC + lattices across gru and sjc, with its slice plan):
[`multi-region-replication.md`](multi-region-replication.md). It replaces the replication described below.

Two regions accepting one database name keep both databases: [database name conflicts](database-name-conflicts.md).
Renaming a database, and what concurrent cross-region renames converge to: [database rename](database-rename.md).

Replication setup, certificates, limits and metrics: [`docs/terrabase/replication.md`](replication.md).
`GET /replication` on the internal listener reports outbound per-peer
and per-database durable lag, detected journal conflicts and catch-up times; see
[status and alert thresholds](replication.md#replication-status-and-alerts).

## API reference

Public operations follow the project hierarchy in [the route table](api.md). They accept ts-auth project
access JWTs (`project_id` and scoped permissions), validated against the configured platform issuer's JWKS.
Customer application tokens from project tenant issuers do not authenticate TerraScale APIs. Existing
organization routes and `tb_` document keys remain hidden compatibility paths until COS confirms migration;
this release preserves existing catalog ids, data files and replication identities.

The client listener serves the public API reference without credentials:

- `GET /` redirects (`302`, relative `Location: scalar/`) to the Scalar reference at `/scalar/`.
- `GET /openapi/v1.json` is the OpenAPI document, generated by ASP.NET Core OpenAPI.

The admin listener serves neither (`404`). See [the API reference](api-reference.md).

## Region identity

Every client response includes `X-TerraBase-Region`. A request with an empty,
duplicate, or mismatched `X-TerraScale-Region-Pin` receives `421 Misdirected Request`
(`invalid_region_pin` or `region_pin_mismatch`, with recovery guidance) and the node's region header; TerraBase does not
forward it. `/healthz` and
management database responses include `region`, identifying the responding node.

Clients resolve endpoints through Atlas, support an explicit static endpoint
override, and pin a session to the selected region. Keep regional endpoints out
of client code. See [the region contract](region-identity.md) for pinning
and Atlas compatibility, and [the region catalog](region-catalog.md) for the serving regions.

## Organization resource admission

Requests authenticated with project JWTs or document credentials share their organization's
[plan resource policy](qos.md): rate and concurrency admission, SQLite read scheduling,
query costs, memory/cache shares and live-document storage quotas. The platform publishes the
resource plan using the trusted platform operator; public user tokens cannot grant
Enterprise admission. Rate, concurrency and memory refusals include `Retry-After`.
Monthly [operation allowances](operation-allowance.md) stop new puts and patches at the plan's
hard allowance; reads and deletes remain available.
New databases stop at the plan's [database allowance](plan-database-allowance.md) (`409 database_limit`).
[Point-in-time restore admission and retention](pitr-plan-entitlements-2026-10-24.md) use those same plan entitlements.
Usage document counts and bytes come from [maintained per-collection counts](collection-counts.md), never from scans.
Customers scrape a project's analytics with Prometheus, and the dashboard reads analytics and logs internally: [metrics export](metrics-export.md).

## Node pressure

`GET /v1/admin/node/pressure` is available only on the internal listener, and pressure metrics on the internal and
metrics listeners. Instance identity, local readiness, actual storage admissions
and outgoing durable ACK lag are sampled diagnostics. Certified membership, incoming
catch-up and other unmeasured safety signals remain unknown, keeping autonomous scaling gated.
See [signal definitions and follow-ups](node-pressure.md).

## Production replication check

The replication end-to-end suite runs against the disposable ts-orch service ([e2e-ts-orch.md](e2e-ts-orch.md)).

## Layout

Optional declared JSON Schema per collection: [attach, validate, and detach](collection-schemas.md).
Per-collection TTL: [declare an expiry member; the home region sweeps expired documents](collection-ttl.md).

Shape data of dropped or recreated collections is retired by an always-on sweep: [shape sweep](shape-sweep.md).

Export a database as NDJSON, resumable by cursor: [export](export.md).

Export a database on a schedule to the project's own bucket: [scheduled exports](scheduled-exports.md).

Import NDJSON (an export, or mongoexport-style documents) through the import pipeline: [NDJSON import](import-ndjson.md).

Relational SQL (tables, joins, transactions) and libSQL clients over Hrana: [design](sql/relational-sql.md).
SQL indexes and batch-write costs: [indexes](sql/indexes.md).
SQL point reads and writes by a TEXT primary key, served by the id index: [key point reads](sql/key-point-reads.md).
Repeated SQL reads reuse their parse and compilation: [read cache](sql/read-cache.md).
Adding nullable SQL columns: [ALTER TABLE ADD COLUMN](sql/alter-table-add-column.md).
The read-only SQLite catalog (sqlite_master, catalog pragmas): [catalog](sql/catalog.md).

Feature-sliced: `src/TerraBase/Features/<Feature>/` holds everything
a slice needs. See `AGENTS.md` for the full structure and rules.
