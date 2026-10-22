# Region identity (TS-499)

## Node identity

The node's region is the Fly region it runs in (`FLY_REGION`, see [fly.md](fly.md)); there is no region setting or
enable step. Its bounded syntax is a lowercase ASCII letter followed by letters, digits or hyphens, at most 63
characters, without a trailing hyphen; a malformed value stops the process.

Region identity does not grant database placement, replication rights or a
residency exemption. Peer Hello/Welcome still validates reported regions;
older peers may omit them and are logged as `unknown`. Region is not part of
database or journal identity.

## HTTP contract

Every response on the client listener includes `X-TerraBase-Region: <region-id>` and `X-TerraBase-Machine: <machine-id>`
(`FLY_MACHINE_ID`, `local` off Fly),
including authentication failures, missing routes, and pin rejection responses.
Clients may send `X-TerraScale-Region-Pin: <region-id>` with the raw region ID.
The pin is optional; when present, it must contain exactly one nonempty value
matching the configured node region. An empty, duplicate, or mismatched pin
returns `421 Misdirected Request` with the correct `X-TerraBase-Region` header.
TerraBase does not proxy or forward the rejected request; behind the edge a
mismatched pin naming a single well-formed region also answers
`ts-replay: region=<pin>`, so the edge replays it there ([fly.md](fly.md#ts-replay)).

### Regional hosts

`<region>.api.terrascale.tech` (for example `gru.api.terrascale.tech`) forces a region; `api.terrascale.tech` goes to
the nearest region, as before. The host is matched case-insensitively and its port is ignored. An explicit regional host
**fails closed**: it is answered in its region or refused, never answered by another region.

| The request | Answer |
| --- | --- |
| lands in the host's region | served (a replayed one too) |
| lands elsewhere, and the region has a started machine (Fly's machine list) | `421`, `ts-replay: region=<region>;fallback=self`: the edge replays it there |
| the edge replayed it and it still landed elsewhere (`ts-replay-src`), or the edge's replay reached no machine there and came back (`ts-replay-failed`), or the region has no started machine | `503 region_unavailable`, naming the region and listing the regional hosts; no replay, so no loop |
| the label is not a region (`gru_1.api…`, `a.b.api…`) | `421 misdirected_request`, listing the regional hosts; the label is not echoed |
| carries `X-TerraScale-Region-Pin` naming another region than the host | `421`, no replay: the two pins contradict each other |

A region whose machines are all stopped cannot be told from one this app was never deployed to (Fly's internal DNS
lists started machines only), so both answer `503 region_unavailable`. A request whose host is not a regional host keeps
the header pin's rules above. A `200` over `sjc.api.terrascale.tech` proves DNS and TLS for that name only; that the
answer came from sjc is shown by `X-TerraBase-Region: sjc`.

The `/healthz` JSON body includes `status`, `version`, `region`, `storageFormat`, `revision`: the source commit the build was made from (`TERRABASE_REVISION` in CI images, null when a build recorded none), and `machine` (`FLY_MACHINE_ID`, `local` off Fly), so each answer names the exact machine and commit (`/readyz` and `/backupz` carry the same fields).
Management API database JSON includes `region` on create, branch, get, and list
responses. This value is the responding node's region at response time, including
for existing database records; it is not persisted placement metadata and
requires no storage migration. Each machine answers Management requests from its own
catalog. A Management commit records its response once, so its `region` names the
machine that applied it.

## Client routing and Atlas compatibility

Clients resolve regional endpoints through ts-atlas, allow an explicitly
configured static endpoint override, and retain the selected endpoint and
region pin for the session. A static override bypasses discovery. Regional
endpoint lists must not be hardcoded in client code. A `421` is a wrong-region
signal for the client to handle through discovery or its override policy.

TS-496 owns Atlas discovery and session pinning for the C# TerraBase contract.
As checked on 2026-10-04, Ku reports TS-496 as **In Progress**, with no comments;
`fj pr search -s all TS-496` in ts-atlas returned no matching PR. The inspected
Atlas `origin/main` commit was `fb3ce7803b206395c4b0be59c740eaff489e8e19`;
its source and contracts have no `X-TerraScale-Region-Pin` implementation.
TS-499 therefore uses the fallback header above, not a signed token or encoded
pin. Reconcile it with TS-496 when that contract lands.

Atlas's current `validRegionCode` accepts uppercase region codes such as `GRU`
(`src/geo.ts`); regional hostnames are lowercase. TS-496 must align discovery
region IDs with TerraBase's final setting syntax before raw pins interoperate.

Atlas currently stamps `X-TerraScale-Api-Region` on proxied origin requests and
`X-TerraScale-Serving-Region` on proxy responses (`src/regional-proxy.ts`,
`README.md`). Those routing annotations are distinct from TerraBase's node
identity response header and the client's region pin.

## Probe dependency

[TS-483 / TerraBase PR #16](https://git.terrascale.tech/TerraScale-Old/terrabase-premonorepo/pulls/16)
was still open when checked on 2026-10-04. It owns public `/healthz` and `/readyz`
listener exposure and readiness checks; TS-499 does not duplicate that work.
Once composed with TS-483, both probe bodies must include `region` alongside
`status` and `version`. Until then, `/healthz` remains administrative-only and
this change does not add `/readyz` or claim storage readiness.

The inspected Atlas `origin/main` still uses `/v1/openapi.json` for its TerraBase
mutation readiness probe (`API_PROXY.readyPath` in `src/regional-proxy.ts`).
Switching Atlas to a public `/readyz` requires the TS-483 contract to land first.
