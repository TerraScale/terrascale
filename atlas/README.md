# Atlas

Atlas at `https://atlas.terrascale.tech` tells SDKs which region holds each database and signs short-lived region pins. It runs independently of TerraBase using D1, one Durable Object writer and KV.

## Routes

Every route supports wildcard CORS and local OPTIONS preflights. Read responses use `Cache-Control: no-store`; successful `/v1/keys` responses use `public, max-age=300`. Errors use `{ "error": { "code", "message", "fix" } }`. The deprecated `/api/v1/*` routes are intentionally not rebuilt.

### Discovery

```sh
curl https://atlas.terrascale.tech/v1/discovery
```

Returns `{v, updated, stale, closest, regions, services}`. Regions include `health` (`ok`, `unknown`, `down`) and `distanceKm` (null without a client location). Order is health, great-circle distance from Cloudflare coordinates or continent, then publication order. `closest` is the first region that is not down, or null. After `STALE_AFTER_S`, health becomes unknown. `stale` means a cached copy was served because D1 failed; topology age independently controls health.

### Resolve

```sh
curl "https://atlas.terrascale.tech/v1/resolve?project=p1&database=orders&fresh=1"
```

Returns `{v, project, database?, region, api, auth, health, placement, stale, pin, expires}`. A database placement selects its region even when down (`placement: "known"`). Omitting the database selects the closest usable region (`placement: "any"`). A named database with a missing placement or tombstone returns `503 placement_unavailable`; it never selects an arbitrary region. A placed region absent from topology returns `503 no_eligible_region`. An unreadable placement fails with `503 discovery_unavailable`.

Project IDs are 1–128 ASCII letters, digits, underscores or hyphens, beginning with a letter or digit (including UUIDs, ULIDs and `p1`). Database names match `^[a-z][a-z0-9_-]{0,62}$`. Region/service IDs are 1–63 lowercase letters, digits or hyphens, beginning with a letter and ending with a letter or digit. Invalid resolve parameters return `400 invalid_project` or `invalid_database`.

Pins are compact ES256 JWS, raw 64-byte r||s, with RFC 7638 public-key thumbprint `kid`. Claims are `iss: "atlas.terrascale.tech"`, project, database when supplied, region, api, iat and exp (`iat + PIN_TTL_S`). Missing or invalid signing configuration returns `503 pin_unavailable`.

### Pin verification keys

```sh
curl https://atlas.terrascale.tech/v1/keys
```

Returns a JWKS containing the public P-256 signing key, `alg: "ES256"`, `use: "sig"` and its thumbprint `kid`. Never returns the private scalar. Re-fetch on an unknown kid and resolve again when pins expire.

### Publish

```sh
curl --request POST https://atlas.terrascale.tech/v1/publish \
  --header "Content-Type: application/json" \
  --header "X-Atlas-Timestamp: $ATLAS_TIMESTAMP" \
  --header "X-Atlas-Signature: $ATLAS_SIGNATURE" \
  --data-binary @publish.json
```

The publisher signs the exact UTF-8 bytes of `"atlas-publish-v1\n" + timestamp + "\n" + body` using ES256, then base64url-encodes raw r||s. Timestamp must be within 300 seconds. Malformed headers are rejected before reading the body; reading is limited to 128 KiB and 10 seconds.

Body shape:

```json
{
  "topology": {
    "v": 1,
    "updated": 1791159807,
    "regions": [
      {
        "id": "gru",
        "name": "São Paulo",
        "country": "BR",
        "lat": -23.43,
        "lon": -46.47,
        "api": "https://gru.api.terrascale.tech",
        "auth": "https://gru.auth.terrascale.tech",
        "ok": true
      }
    ],
    "services": [
      {
        "id": "terrabase",
        "name": "TerraBase",
        "url": "https://api.terrascale.tech",
        "health": "https://api.terrascale.tech/readyz"
      },
      { "id": "auth", "name": "Auth", "url": "https://auth.terrascale.tech" },
      { "id": "dashboard", "name": "Dashboard", "url": "https://dash.terrascale.tech" },
      { "id": "status", "name": "Status", "url": "https://status.terrascale.tech" },
      { "id": "docs", "name": "Docs", "url": "https://docs.terrascale.tech" }
    ]
  },
  "placements": [{ "v": 1, "project": "p1", "database": "orders", "region": "fra", "updated": 1791159807 }]
}
```

Send topology, placements, or both. Topology has 1–64 unique regions and at most 32 unique services, including terrabase, auth, dashboard, status and docs. Region api must match `^https://[a-z0-9-]+\.api\.terrascale\.tech$`; auth must be an HTTPS TerraScale origin. Service URLs may have paths but must be HTTPS on terrascale.tech or a subdomain. Credentials, ports, queries and fragments are refused. Only with `DEV_ORIGINS === "1"`, explicit-port HTTP localhost and 127.0.0.1 URLs are also accepted.

At most 64 distinct `(project, database)` placements per request. `region: null` deletes a placement while retaining its timestamp. Duplicate database keys, invalid URLs or missing required services reject the entire request before any write. `updated` is a nonnegative Unix second and cannot exceed the clock by more than 300 seconds (`400 future_updated`).

Topology older than the stored record, or different at the same timestamp, returns `stale`; an identical record at the same timestamp returns `skipped`. A newer topology returns `updated`. Placements return `updated` for a newer timestamp or an identical record at the same timestamp (including null regions), refreshing KV. Older timestamps or different regions at the same timestamp return `stale`, including a replay predating a tombstone. Response: `{ "topology": "updated|stale|skipped", "placements": [{ "project", "database", "result": "updated|stale" }] }`, with topology omitted when absent and placements an empty array when absent.

Topology-only bodies require a key in `TOPOLOGY_PUBLISH_KEYS`; placement-only bodies require one in `PLACEMENT_PUBLISH_KEYS`. Combined bodies require the same key in both lists. Both settings are JWKS JSON strings with 1–4 public P-256 keys; private keys are rejected. Invalid signatures return `401 invalid_signature`, missing authorized key configuration `503 publish_disabled`, invalid bodies `400 empty_publish`, `invalid_topology`, `invalid_placement` or `future_updated`, oversized bodies `413 payload_too_large`, and slow bodies `408 body_timeout`.

## Storage and caching

`schema.sql` creates:

- `topology(id INTEGER PRIMARY KEY CHECK(id=1), updated INTEGER NOT NULL, doc TEXT NOT NULL)`.
- `placements(project TEXT NOT NULL, database TEXT NOT NULL, region TEXT, updated INTEGER NOT NULL, PRIMARY KEY(project, database))`.

Every publish uses `ATLAS_STORE.idFromName("atlas")`. Its queue serializes whole publishes across asynchronous D1 and KV operations. Placements run in groups of eight, retaining input order; each group settles before a failed publish releases the writer queue. D1 is written before KV. `502 store_unavailable` means D1 refused a read/write needed by the publish or the Durable Object request threw; earlier items may have landed. A KV refresh failure is logged without misreporting an accepted D1 write as failed. An identical topology or placement publish can repair its KV copy.

Read order is isolate memory (5 seconds), KV (60-second freshness bound), then D1 with a 2.5-second deadline and KV refill. Keys are `topology` and `placement:<project>:<database>`, containing `{at, doc}`; null records cache an absent row, while deleted placements cache their tombstone. Cache memory is bounded to 512 entries. Local publishes invalidate isolate memory. Other isolates and KV propagation converge within the freshness bound. D1 errors or timeouts serve the last memory copy, otherwise KV even beyond its freshness bound, with `stale: true`; no copy returns `503 discovery_unavailable`. `fresh=1` bypasses memory and KV for the initial read but can serve them as stale if D1 fails or times out. `X-TerraScale-Cache` is HIT for memory/KV, MISS if any required D1 read answered.

## Bindings and secrets

| Name                   | Kind           | Value                                      |
| ---------------------- | -------------- | ------------------------------------------ |
| ATLAS_DB               | D1             | ts-atlas database                          |
| ATLAS_KV               | KV             | read cache namespace                       |
| ATLAS_STORE            | Durable Object | AtlasStore, single named instance atlas    |
| TOPOLOGY_PUBLISH_KEYS  | var            | public P-256 JWKS JSON, 1–4 keys           |
| PLACEMENT_PUBLISH_KEYS | var            | public P-256 JWKS JSON, 1–4 keys           |
| PIN_SIGNING_KEY        | secret         | private P-256 JWK for session pins         |
| DEV_ORIGINS            | local var      | `"1"` permits loopback URLs in development |
| STALE_AFTER_S          | var            | 60                                         |
| PIN_TTL_S              | var            | 600                                        |
| VERSION                | var            | dev locally; production source revision    |

## Local development

```sh
npm ci
npx wrangler d1 execute ts-atlas --local --file schema.sql
npm run dev
```

Put temporary development JWKS and a private pin JWK in ignored `.dev.vars`; never commit it. `npm run check`, `npm run lint` and `npm run build` validate the package. Build audits dependencies before bundling. No GitHub workflow deploys Atlas.

## Operators: first deploy

1. Use a green, merged main revision. Create the `ts-atlas` D1 database and an Atlas KV namespace through the approved Cloudflare operator process. Replace the zero placeholder IDs in `wrangler.jsonc` with their real IDs through a PR before deploying.
2. Set both publisher JWKS vars to the authorized orchestrator public keys. Keep the publisher private key with its operator. Provision a separate P-256 private pin JWK as the `PIN_SIGNING_KEY` Worker secret using Wrangler stdin or its interactive prompt; never put it in a command argument or tracked file. Do not set DEV_ORIGINS in production.
3. Apply `schema.sql` to remote D1 with `npx wrangler d1 execute ts-atlas --remote --file schema.sql`. The schema is idempotent; no data reset is required.
4. Run `npm ci`, `npm run check`, `npm run lint` and `npm run build`. Verify the generated `dist/ts_atlas/wrangler.json` retains the real bindings, custom domain, disabled workers.dev/preview URLs and v1 SQLite Durable Object migration.
5. Deploy manually from that approved revision with `npx wrangler deploy -c dist/ts_atlas/wrangler.json --experimental-provision=false --experimental-auto-create=false --var VERSION:<full-source-sha>`. The custom domain is atlas.terrascale.tech. There is deliberately no deploy workflow or automatic deployment hook.
6. Publish a current signed topology and database placements; inspect discovery, resolve (including a fresh resolve), and keys. Verify a returned pin with the public JWKS before using Atlas in SDKs. Keep this first-deploy proof with the release record.
