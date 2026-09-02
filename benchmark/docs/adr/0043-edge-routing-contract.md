---
status: accepted
authority: ../../../GOAL.md
---

# Edge routing contract: placement headers, `421`, and `/regions`

## Decision

The member is the source of truth for where a database lives; the edge
(`ts-proxy`, GOAL D-58) and the SDK are caches of that truth that repair
themselves from every response. Three pieces of the public API exist for
this and nothing else.

### 1. Placement headers on every database-scoped response

Every response to a request under `/api/v1/databases/{id}`,
`/api/v1/tenants/{key}`, or `/api/v1/reactive?db=` carries:

```http
X-TerraScale-Placement: GRU,FRA
X-TerraScale-Residency: eu-only        # omitted when unconstrained
X-TerraScale-Served-By: FRA
```

Region codes are ADR 0033 `RegionCode`s, comma-separated, in placement order
(the first is the region the database was created in). The headers are set
from the member's placement table (fed by the orchestrator over control gRPC),
never from request input, and are present on error responses too.

### 2. `421 Misdirected Request` for databases this region does not host

A member that receives a database-scoped request for a database it does not
host in its region answers:

```http
HTTP/1.1 421 Misdirected Request
X-TerraScale-Placement: GRU,FRA
X-TerraScale-Residency: eu-only
Content-Type: application/json

{ "error": "misdirected", "message": "database 01J… is not hosted in SFO",
  "placement": ["GRU", "FRA"], "residency": "eu-only",
  "fix": "send this request to gru.api.terrascale.tech or fra.api.terrascale.tech" }
```

The member does **not** proxy to the right region itself: that would hide
misrouting, add a WAN hop on every stale-cache request, and let a client keep
talking to the wrong region indefinitely. `421` is authoritative and cheap
(a placement-table lookup, no database open). A member that hosts the
database but is not its in-region writer serves reads it can serve and
forwards writes in-region as before (GOAL §2); that is not a `421`.

Unknown database ids (never existed, deleted past soft-delete) are `404`,
not `421`, so callers can distinguish "wrong region" from "gone".

### 3. `GET /api/v1/regions`

On a member: `{ "region": "FRA", "ok": true, "lag_ms": { "GRU": 120, "SFO": 210 } }`
— this member's own view. On `api.terrascale.tech` the edge answers the
client-relative ordered list (ts-proxy design §7). The SDK uses the edge
form; the member form exists for probes and `terrascale doctor`.

## SDK behaviour (normative for `@terrascale/sdk`)

1. On first use, `GET api./regions`; cache the ordered list for 5 minutes,
   refresh on any transport failure.
2. First request for a database goes to `api.`; read
   `X-TerraScale-Placement`; store per database.
3. Pin to the first region in the client's ordered list that appears in the
   placement; send all further requests for that database to
   `<region>.api.`; carry the session token.
4. On `421`: update placement from the header, re-pin, retry once
   transparently; surface nothing unless the retry also fails.
5. On connection failure or `503` from the pinned region: move to the next
   region in placement order-by-distance, retry idempotent requests
   immediately and mutations with their idempotency key; refresh `/regions`.
6. `regionHint` is honoured only if that region is in the placement;
   otherwise ignored with a debug log, never an error.
7. Reactive connections are opened against the pinned region hostname with
   `?db=`; a `421` on the upgrade is handled like any other.

## Consequences

- Placement lookup must be O(1) and never open a database file; the
  placement table is in memory on every member and updated by control gRPC.
- The headers add ~40 bytes per response; acceptable.
- `421` and the placement headers are part of the OpenAPI document and thus
  of the generated SDK types; the journey (SHORT_GOAL §4 step 17) proves the
  loop end to end.
- Residency (GOAL D-52) is enforced here as well: a member outside the
  residency set never has the database in its placement table, so it can
  only ever answer `421`.
