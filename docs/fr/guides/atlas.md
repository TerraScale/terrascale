---
title: Atlas discovery
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

# Atlas discovery

Configure the candidate TerraBase SDK with an explicit HTTPS endpoint or a
caller-supplied resolver. Automatic Atlas integration is deferred; the
contract below describes the Atlas candidate, not behavior already shipped
in every SDK. See [SDK compatibility](/fr/guides/sdks) and the
[C# quickstart](/fr/getting-started/csharp).

::: warning Candidate contract
Atlas discovery and the connected SDK/production journey still require
release and deployment qualification. The candidate targets project-scoped
C# TerraBase APIs, not the retired native UUID/shard admission protocol.
Do not infer availability from this page or substitute a legacy discovery route.
:::

## Public discovery routes

The Atlas base URL is `https://atlas.terrascale.tech`. These candidate read
routes do not require a database credential:

| Route | Result |
| --- | --- |
| `GET /v1/discovery` | Ranked `regions`, the closest usable region in `closest`, and the public `services` catalog |
| `GET /v1/resolve?project=<id>&database=<name>` | Project/database placement, `region`, `api`, `auth`, `health`, `placement`, `stale`, signed `pin`, and `expires`; `database` is optional |
| `GET /v1/keys` | Public JWKS for verifying session pins |

`project` is the project identifier in
`/v1/projects/{project}/databases/{database}/…`; `database` is its name.
A database placement overrides the project default. An unplaced project gets
the closest usable region with `placement: "unknown"`. A placed project keeps
its assigned region even when that region is down: ranking does not move data.

Discovery ranks healthy regions before unknown and down regions, then by
client distance. `closest` can be `null`. Responses expose freshness through
`stale`; stale health becomes unknown. If discovery has no usable metadata,
it returns `503 discovery_unavailable`. An assigned region missing from the
catalog returns `503 no_eligible_region`; unavailable signing returns
`503 pin_unavailable`. Treat these as resolution failures, not permission to
pick an arbitrary regional host.

`/api/v1/regions` and `/api/v1/auth/discovery` are deprecated compatibility
routes. New consumers use `/v1/discovery` and `regions[].auth`. The retired
native `/api/v1/discovery` admission request is a different contract.

## Session pin contract

A resolve response carries a compact JWS signed with ES256 (ECDSA P-256 and
SHA-256), valid for 600 seconds. Its header includes `kid`; its claims bind
`iss: "atlas.terrascale.tech"`, `project`, optional `database`, `region`, `api`,
`iat`, and `exp`. The routing pin is not a database access token and grants
no document permissions.

An Atlas-aware integration must:

1. Resolve the exact project/database and verify the ES256 signature with
   `/v1/keys`, including issuer, scope, origin and expiry. Refresh JWKS on an
   unknown `kid`.
2. Use the returned `api` and send `X-TerraScale-Region-Pin: <region>`.
   This header contains the region identifier, not the compact JWS.
3. Re-resolve with `fresh=1` when the pin expires, on a `421` response, or on
   a redirect. Disable automatic redirects before sending credentials; a
   redirect does not authorize forwarding them. Do not automatically replay
   a mutation with an uncertain outcome.

The candidate TerraBase region check returns `421` and `X-TerraBase-Region`
for a mismatched region pin. Both this check and SDK pin handling need matched
release qualification. A caller implementing caching must never extend a pin
past its signed expiry.

## Configure today's SDK candidates

| SDK | Explicit endpoint | Caller-supplied resolver |
| --- | --- | --- |
| C# | `TerraBaseDocumentsClientOptions.BaseAddress` | `EndpointResolver` implementing `ITerraBaseEndpointResolver` |
| JavaScript | `createTerraBaseV1Client({ baseUrl, … })` | `endpointResolver`, receiving `{ project, database }` |
| Go `terrascale.tech/sdk/v1` | `Config.BaseURL` | `Config.Resolver` implementing `Resolver` |

Set exactly one endpoint option. C# and Go resolve per request; JavaScript
reuses resolution within a database session. These resolver seams do not
implement Atlas verification, pin headers or refresh automatically. The
C# resolver receives cancellation rather than project/database arguments;
bind its intended scope in the caller's configuration. Use a static configured
endpoint for the isolated C# qualification lane.

No default production endpoint, catalog fallback, proxy fallback or
cross-region mutation retry is promised by these SDK candidates. A configured
endpoint proves only that endpoint's lane, not Atlas integration.

## Authentication boundary

The candidate Documents client accepts historical `tb_` keys for isolated
compatibility tests. The product design accepts ts-auth JWTs; that production
JWT and authorization journey remains a separate qualification gate. Never
use an Atlas pin as either credential.

Auth owns users, organizations and projects; application data belongs in
TerraBase. Keep dashboard sign-in on its native pages. Discovery selects
service endpoints; it does not create identities, duplicate management models,
or authorize a project.
