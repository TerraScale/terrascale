# TerraScale status catalog: components, regions and endpoints

Use this repo skill to inspect or change what https://status.terrascale.tech
measures. Every change goes through the admin page,
https://status.terrascale.tech/admin (Catalog). Sign in through ts-auth with
owner or admin membership in the TerraScale organization. There is no API
token and no direct D1 edit.

## Model

- **Groups** order components on the page (TerraBase, Platform).
- **Components** are what customers use. "Active" ones are probed; "Coming
  soon" ones are shown and never probed (the dashboard today). An active
  component without endpoints shows "Pending launch" until its first passing round.
- **Regions** use deployment codes: `GRU` São Paulo, `SJC` San Francisco Bay Area,
  `FRA` Frankfurt. Map x/y place the hub on the 136×80 region map.
- **Endpoints** are one check each. A regional endpoint names its region and
  uses a verified public origin; global ones have no region. `provider` is
  `cloudflare` or `other` (including OCI). Production runs in OCI gru under
  ts-orch; do not infer regional hosts from a region code.

Every endpoint carries a check:

- `expectStatus`: `2xx`, or codes such as `200,204`.
- `keyword`: text the body must contain.
- `assertions`: a JSON list of `{"path", "op", "value"}` on the JSON body;
  `op` is `eq`, `ne`, `lt`, `gt`, `exists` or `contains`, and paths are dotted
  keys with array selectors such as `regions[code=GRU].available`. Regional
  readiness asserts `{"path":"region","op":"eq","value":"gru"}` so another
  region answering fails.
- `extract`: named paths recorded with every sample; a changing `version` is
  recorded as a deploy.
- `indirect`: the URL reports on the component (Cloudflare's status page for
  Email), so failing to reach it is unknown, not down.
- `blockedStatus`: a status something in front of the service is known to
  refuse probes with; it counts as a probe error, not an outage.
- `kind` `canary` runs in the coordinator with a secret keyed by endpoint ID
  (`DATA_API_CANARY`, or `TERRABASE_PROBES` with probe `documents`).

## Add a region

1. Add the region (code, name, country, map position).
2. Add its endpoints: copy each gru endpoint, change the region, cluster,
   verified public origin and `region` assertion; never invent a routing header.
3. Press "Check now" on each new endpoint. New endpoints show "Pending launch"
   until their first passing round, then count toward uptime.

## Remove

Deleting an endpoint or component deletes its probe history. Deleting is
refused while an automatic incident is open for the component: resolve it
first. Verify every change on https://status.terrascale.tech/api/v1/status.
