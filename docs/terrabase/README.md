# TerraBase

Read the [required zero-configuration operating contract](zero-configuration.md).
Current configuration paths described below are implementation limitations, not
production activation instructions.

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
   `src/TerraScale.TerraBase/TerraBase.csproj` to the new version, e.g. `0.2.0`.
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
`terrabase-linux-arm64.sha256`) in the directory that holds the binary. See `docs/terrabase/ci.md` for the
pipeline.

## Configuration

Production identity, membership, region discovery, listeners and backup are
automatic under the [operating contract](zero-configuration.md). OCI Vault
provisions and delivers authentication, TLS, persistence and backup credentials.
Limits and timeouts have one correct setting in code.

**Current gap (TS-773):** the runtime still parses `TERRABASE_*`/`--name`
options and guards listeners, authentication, backup and cross-region replication
on their presence. The docs change does not remove those paths or establish
an automatic production bootstrap. Preserve validation and secret-free errors
while replacing them; missing authentication must never grant access.

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
  Completed filesystem results are cached for one second in the current implementation,
  to bound disk work from anonymous traffic. Concurrent callers await the same check; they never receive a stale
  cached result while it runs. A running check becomes unavailable after two seconds. Shutdown immediately
  returns 503 regardless of cached or in-flight results; engine closure is checked on every request.

Point load balancers and reverse proxies (Caddy) at the client listener's `/readyz`.

Replication setup, certificates, limits and metrics: [`docs/terrabase/replication.md`](replication.md).
`GET /replication` on the loopback administrative listener reports outbound per-peer
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
with the node's region header; TerraBase does not forward it. `/healthz` and
management database responses include `region`, identifying the responding node.

Clients resolve endpoints through Atlas, support an explicit static endpoint
override, and pin a session to the selected region. Keep regional endpoints out
of client code. See [the region contract](region-identity.md) for pinning
and Atlas compatibility.

## Organization resource admission

Requests authenticated with project JWTs or document credentials share their organization's
[plan resource policy](qos.md): rate and concurrency admission, SQLite read scheduling,
query costs, memory/cache shares and live-document storage quotas. The platform publishes the
resource plan using the trusted management service credential; public user tokens cannot grant
Enterprise admission. Rate, concurrency and memory refusals include `Retry-After`.

## Node pressure

`GET /v1/admin/node/pressure` and pressure metrics are available only on the loopback
administrative listener. Instance identity, local readiness, actual storage admissions
and outgoing durable ACK lag are sampled diagnostics. Certified membership, incoming
catch-up and other unmeasured safety signals remain unknown, keeping autonomous scaling gated.
See [signal definitions and follow-ups](node-pressure.md).

## Production replication check

`scripts/terrabase/prod-replication-check` writes to the configured canary database through the API, then exits non-zero when any
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

Optional declared JSON Schema per collection: [attach, validate, and detach](collection-schemas.md).

Feature-sliced: `src/TerraScale.TerraBase/Features/<Feature>/` holds everything
a slice needs. See `AGENTS.md` for the full structure and rules.
