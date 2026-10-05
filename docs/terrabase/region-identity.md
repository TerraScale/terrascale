# Region identity (TS-499)

## Node configuration

Set `TERRABASE_REGION` or `--region=<region-id>`; the argument wins. Use the
region ID published by Atlas. A region is required outside the Development
environment; missing or invalid configuration stops startup with exit code `2`.
Development defaults to `development` when no region is configured.
Use a lowercase ASCII letter followed by lowercase letters, digits or hyphens,
at most 63 characters, without a trailing hyphen (for example, `gru-1`).
Select Development with the standard `DOTNET_ENVIRONMENT=Development` host setting.

Region identity describes the running node. It does not establish database
placement, replication, residency, or regional availability.

## HTTP contract

Every response on the client listener includes `X-TerraBase-Region: <region-id>`,
including authentication failures, missing routes, and pin rejection responses.
Clients may send `X-TerraScale-Region-Pin: <region-id>` with the raw region ID.
The pin is optional; when present, it must contain exactly one nonempty value
matching the configured node region. An empty, duplicate, or mismatched pin
returns `421 Misdirected Request` with the correct `X-TerraBase-Region` header.
TerraBase does not proxy or forward the rejected request.

The `/healthz` JSON body includes `status`, `version`, and `region`.
Management API database JSON includes `region` on create, branch, get, and list
responses. This value is the responding node's region at response time, including
for existing database records; it is not persisted placement metadata and
requires no storage migration.

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

[TS-483 / TerraBase PR #16](https://git.terrascale.tech/TerraScale/terrabase/pulls/16)
was still open when checked on 2026-10-04. It owns public `/healthz` and `/readyz`
listener exposure and readiness checks; TS-499 does not duplicate that work.
Once composed with TS-483, both probe bodies must include `region` alongside
`status` and `version`. Until then, `/healthz` remains administrative-only and
this change does not add `/readyz` or claim storage readiness.

The inspected Atlas `origin/main` still uses `/v1/openapi.json` for its TerraBase
mutation readiness probe (`API_PROXY.readyPath` in `src/regional-proxy.ts`).
Switching Atlas to a public `/readyz` requires the TS-483 contract to land first.
