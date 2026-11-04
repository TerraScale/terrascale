# TerraBase

Read the [required zero-configuration operating contract](zero-configuration.md).
Current configuration paths are implementation limitations, not production activation instructions.

Hosting: [OCI and ts-orch](../oci.md); node discovery: [TerraBase on ts-orch](ts-orch.md).
Local processes: [TerraBase local host](local-host.md).
Recovery: [Copy placement and snapshot routing](recovery-copy-placement.md).
Transport security: [TLS and authentication on every SQL surface](transport-security.md).
Erasure: [Crypto-shredding of deleted databases](crypto-shredding.md).
Clock safety: [HLC and clock admission](hlc-clock.md).
Placement: [Home region, replica regions, all-regions mode and moving the home](placement.md).

TerraBase is TerraScale's database, moving to SQL only on Turso. The leaderless
replication engine is removed; each database serves from one region until region
replicas ship ([decision](decisions/2026-10-29-single-region-until-sync.md)).
The document API is still served until its deletion lands. This is the C#/.NET 11
implementation.

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
options and guards listeners, authentication and backup
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

Each database is served by its home region only ([cross-region.md](cross-region.md),
[decision](decisions/2026-10-29-single-region-until-sync.md)). The leaderless replication engine, its peer streams,
anti-entropy and `GET /replication` status are removed. Superseded designs are kept in [archive/](archive/README.md).
Region replicas (read-only page-pull copies in replica regions, PIVOT R3): [`region-replicas.md`](region-replicas.md).
Home apply of transactions committed in other regions, `terrascale.conflicts` and write states (PIVOT R6): [`home-apply.md`](home-apply.md).
Home leases, failover, fencing epochs and the `durability` modes with their RPO (PIVOT R8): [`home-failover.md`](home-failover.md).
Local writes in a replica region, the local overlay, the push stream to the home and `durability=home` (PIVOT R7): [`local-overlay.md`](local-overlay.md).
System records (catalog bookkeeping) are plain rows: [plain-row storage](decisions/2026-11-04-plain-row-storage.md).

Catalog of organizations, projects and databases: [SQL catalog](sql-catalog.md).
Renaming a database: [database rename](database-rename.md).

Databases for agents (create with a TTL, fork, scoped keys): [agent databases](agent-databases.md).

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
Monthly [operation allowances](operation-allowance.md) stop new writes at the plan's write cap
and SQL reads at the plan's fair use; shape reads are never counted.
New databases stop at the plan's [database allowance](plan-database-allowance.md) (`409 database_limit`).
[Point-in-time restore admission and retention](pitr-plan-entitlements-2026-10-24.md) use those same plan entitlements.
One process serves many databases: [shared-process tenancy](shared-process-tenancy.md) bounds open writers and
governs statement timeouts, engine time and I/O workers per organization.
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

Transaction change-group budget, rollback and the deterministic `503 capacity` refusal: [transaction capacity](documents/transaction-capacity.md).

End-user shape reads from the browser under table read grants: [shape read grants](shape-read-grants.md).

Live coalescing, per-tenant admission, hint sockets and cache boundaries: [live shape reads](free-reads-live.md).

JavaScript snapshot, catch-up and tail transport: [JS shape CDN reads](pivot/js-shape-cdn.md).

Shape data of dropped or recreated collections is retired by an always-on sweep: [shape sweep](shape-sweep.md).

Export a database as NDJSON, resumable by cursor: [export](export.md).

Export a database on a schedule to the project's own bucket: [scheduled exports](scheduled-exports.md).

Import NDJSON (an export, or mongoexport-style documents) through the import pipeline: [NDJSON import](import-ndjson.md).

SQL runs on real tables in each database's file (tables, indexes, views, triggers, foreign keys, transactions): [native SQL](sql/native-sql.md).

SQL over WebSocket: [protocol](sql/websocket.md).

Writes in a replica region: [write routing](sql/write-routing.md).

Removed protocol surfaces and compatibility notes: [E4a](e4a-removal.md).

Feature-sliced: `src/TerraBase/Features/<Feature>/` holds everything
a slice needs. See `AGENTS.md` for the full structure and rules.

Database region observations and dashboard lag: [`replica-status.md`](replica-status.md).

- [Region shape tails and CDC retention](region-shape-tails-and-cdc-retention.md)

CI: [Fast timing budgets and checked builds](fast-ci-budgets.md).
