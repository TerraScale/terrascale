# Database regions

Each database has a home region and can have encrypted regional replicas. Ready replicas serve supported reads; writes follow the SQL router and either commit through the regional path or forward to home. Routing decisions, commit location and home confirmation are separate facts. See [write routing](sql/write-routing.md), [home apply](home-apply.md) and [region replicas](region-replicas.md).

## Placement

A database record carries a placement (`DatabasePlacement` in `Tenancy/ITenantDirectory.cs`): `Home`, `Replicas`,
`Version` and `AllRegions`. Replica regions hold replicas ([region replicas](region-replicas.md)); the placement API
and moving the home are in [placement](placement.md).

- **Create.** `homeRegion` defaults to the called region. A region TerraBase does not serve, or a value that is not a
  region, answers `400 invalid_request`; the message lists the serving regions for the former. Another serving region answers
  `421 wrong_region` with `ts-replay: region=<home>`, so the edge replays the create there.
- **Replicas.** A create or `PATCH` names `replicaRegions` or `allRegions: true` ([placement](placement.md)).
- **Update.** `PATCH` renames the database or sets its replica regions. `homeRegion` changes only through
  `POST …/move-home`.
- **Home-only operations.** Database-scoped Management requests other than reading, updating and deleting the record
  are checked against the home region (`DatabaseRegions.ElsewhereAsync`).

## Requests outside placement

`UseTerraBaseDatabasePlacement` checks client requests that name a project and database. When a region keeps a
routing record but no longer holds a copy, it proxies the request over the private mesh to the current writer.
The same applies to regions that have never held a copy. REST requests retain their method, body, query and bearer
credential. WebSocket sessions relay frames for the session's lifetime, including browser authentication frames.
The destination enforces the credential's project, database and scopes.

The response carries the destination's `X-TerraBase-Region`. An unavailable destination answers `503 unavailable`;
a write sent without an answer retains `503 write_outcome_unknown`. These paths do not return `wrong_region`.
A placement change while opening a socket requires a reconnect; an active SQL transaction interrupted by a writer
change returns `serialization_conflict`.

The catalog and replication still record the writer separately from the other members. Operations restricted to
the writer can still return placement guidance when called at a region that holds a copy.

## Forwarding to the home

Any region serves any database: a request that lands in a region without the record reaches the home over the private
mesh.

- **REST.** After authentication, a project request (`/v1/projects/{project}/...` with a project token) whose project, or
  named database, is not in this region's catalog asks the other regions' nodes on the internal listener
  (`GET /v1/internal/home`, peer credential) which one holds it, caches the answer for 30 seconds and forwards the request
  there over HTTP/2 with the peer credential and `X-TerraBase-Forwarded-For` (the client address the edge reported). The
  home serves it as a client-listener request: the same token checks, IP allowlists, quotas and metering, once, at the home.
  `X-TerraBase-Region` names the home. A forwarded request is never forwarded again. When the home cannot be reached the
  answer is `503 unavailable`; a write the home was sent
  but never answered is `503 write_outcome_unknown` instead ([unanswered forwards](sql/write-routing.md#unanswered-forwards)). Listing a project's
  databases is forwarded to a region that holds the project when this one does not.
- **Enrollment.** A create that enrolls a project (its first database) sends the same project→organization binding to every
  other region (`PUT /admin/projects/{project}/enrollment`, peer credential, idempotent), so every region resolves the
  project within seconds instead of answering `404 unknown_database` until its replicated catalog converges. A region that
  misses the fan-out keeps resolving through home forwarding; the next first-create retry or enrollment repairs the gap.
- **Postgres wire.** A dashboard API key is exchanged at ts-auth in the region the session reached; when its project or
  database is homed elsewhere, the session is relayed to the home for its whole life (`POST /v1/internal/postgres`, a
  duplex HTTP/2 stream on the internal listener), and the home authenticates it again. An unreachable home answers
  `FATAL 08001` naming the home region; a database that exists in no region answers `FATAL 3D000`. A cancel request sent
  to the relaying region does not reach the home.
- **WebSockets.** A session WebSocket (`sql/ws`, `shape/ws`) whose database is homed elsewhere cannot follow a `421` or
  a forwarded upgrade, so the region relays the whole socket to the home: it opens a WebSocket to the home's client route
  over the mesh (RFC 8441 on the internal listener) with the client's subprotocols, headers and address, then accepts the
  client and pumps frames both ways for the socket's whole life. The home serves it exactly as a socket that reached it
  directly — the same token, IP-allowlist and scope checks, once, at the home. When the home cannot be reached the answer
  is `503 unavailable`, like a forwarded request. Inside a region, the serving peer
  relays sockets to the regional writer the same way ([regional writer](regional-rollout-handoff.md#requests-that-reach-the-serving-peer)).
- **Replica regions.** A Postgres or WebSocket session that reaches a replica region of its database is not relayed: it
  reads from the replica and runs writes and transactions on a session at the home
  ([region replicas](region-replicas.md#writes-and-transactions)).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
- `X-TerraBase-Forwarded-For` and the relay are honored only on the internal listener with a matching peer credential;
  public forwarded headers are never trusted.

## Region discovery

Serving regions come from `TS_REGION` and the ts-orch peers file `TS_PEERS_FILE` ([ts-orch.md](ts-orch.md)). Peers
are used for routing and private listener binding; discovering a region grants no placement and creates no copy.

## Tests
