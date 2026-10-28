# Region identity (TS-499)

## Node identity

Node and region identity must be inferred from the running environment. Verify the
identity reported by the deployed OCI runtime; this document does not prescribe a
provider-specific environment variable or establish that ts-orch migration is complete.
The region's bounded syntax is a lowercase ASCII letter followed by letters, digits
or hyphens, at most 63 characters, without a trailing hyphen; malformed values stop startup.

Region identity does not grant database placement, replication rights or a
residency exemption. Peer Hello/Welcome still validates reported regions;
older peers may omit them and are logged as `unknown`. Region is not part of
database or journal identity.

## HTTP contract

Every response on the client listener includes `X-TerraBase-Region: <region-id>` and `X-TerraBase-Machine: <machine-id>`
(the deployed runtime identity),
including authentication failures, missing routes, and pin rejection responses.
Clients may send `X-TerraScale-Region-Pin: <region-id>` with the raw region ID.
The pin is optional; when present, it must contain exactly one nonempty value
matching the configured node region. An empty, duplicate, or mismatched pin
returns `421 Misdirected Request` with the correct `X-TerraBase-Region` header, before authentication. The body says
how to recover without naming any database or echoing the pin:

| The pin | `error.code` | `error.message` tells the client to |
| --- | --- | --- |
| empty, duplicate, or not a region ID | `invalid_region_pin` | send exactly one region ID; lists the serving regions |
| names a region with no serving machine | `region_pin_mismatch` | pin one of the listed serving regions, or drop the pin |
| names a serving region, but the request reached another (the edge could not answer it there) | `region_pin_mismatch` | retry, call `<region>.api.terrascale.tech`, or drop the pin to reach the nearest region; a database answers only in its regions, so pin one of them or add the pinned region to its placement |

`misdirected_request` stays for an unknown regional host label; a request for a database that is not
placed in the answering region gets `421 wrong_region` with its placement headers instead
([cross-region.md](cross-region.md)). An unavailable regional host is `503 region_unavailable` (below).
TerraBase does not proxy or forward the rejected request; behind the edge a
mismatched pin naming a single well-formed region also answers
`ts-replay: region=<pin>`, requesting replay by the edge ([ts-orch.md](ts-orch.md#ts-replay)).

### Regional hosts

`<region>.api.terrascale.tech` (for example `gru.api.terrascale.tech`) forces a region; `api.terrascale.tech` goes to
the nearest region, as before. The host is matched case-insensitively and its port is ignored. An explicit regional host
**fails closed**: it is answered in its region or refused, never answered by another region.

| The request | Answer |
| --- | --- |
| lands in the host's region | served (a replayed one too) |
| lands elsewhere, and membership lists a node in the region | `421`, `ts-replay: region=<region>;fallback=self`: requests edge replay |
| the edge replayed it and it still landed elsewhere (`ts-replay-src`), or the edge's replay reached no machine there and came back (`ts-replay-failed`), or membership lists no node in the region | `503 region_unavailable`, naming the region and listing the regional hosts; no replay, so no loop |
| the label is not a region (`gru_1.api…`, `a.b.api…`) | `421 misdirected_request`, listing the regional hosts; the label is not echoed |
| carries `X-TerraScale-Region-Pin` naming another region than the host | `421 region_pin_mismatch` (`invalid_region_pin` for a malformed pin), no replay: the two pins contradict each other |

A request whose host is not regional keeps the header pin's rules above.
A `200` over `sjc.api.terrascale.tech` proves DNS and TLS for that name only;
`X-TerraBase-Region: sjc` identifies the answering region. Unavailable regional
routing must fail closed; its OCI ingress behavior requires runtime verification.

On the public listener the `/healthz` JSON body is `status`, `version` (without build metadata) and `region` only; the
answering node is named in `X-TerraBase-Machine`. On the internal listener (peer credential) it also carries
`storageFormat`, `revision`: the source commit the build was made from (`TERRABASE_REVISION` in CI images, omitted when a
build recorded none), and `machine` (the deployed runtime identity), so an operator sees the exact machine and commit.
`/readyz` and `/backupz` follow the same split: anonymous callers never see the revision or storage format.
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
