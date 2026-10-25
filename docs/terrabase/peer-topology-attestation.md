# Peer topology attestation

In a ts-orch cluster, TerraBase reads `topology.json` beside `TS_PEERS_FILE` every
5 seconds, with a two-second comparison budget. At most one comparison can
remain pending if a directory ignores cancellation; its late result is discarded.
ts-orch owns this independent manifest and its durable admitted
identity registry. TerraBase never repairs either file. A missed ping is a
liveness observation, not permission to remove an admitted region or identity.

The manifest has `generation` (positive int64), `observedAt` (UTC RFC3339),
`regions` (unique region strings), and `peers` (unique `{node,region,address}`
identities, including self, one per region, with canonical IPv4 addresses).
Files are limited to 64 KiB and 64 peers. Missing, malformed, incomplete or
duplicate data closes serving. Observations older than 30 seconds or over
5 seconds in the future close serving too.

TerraBase compares the manifest with the strict peers-file contents, current
member directory, loaded membership, actual configured replication links, and
self's address actually bound at startup. Changing self's advertised address
requires a matching listener. Link identities are copied under the replication
lock without refreshing or rewriting links; a temporarily disconnected stream
does not remove its peer. Readiness and client requests fail closed on drift,
as do new wire operations and PostgreSQL messages. Established ordinary and end-user change
feeds recheck before subsequent reads and sends, closing without further data
when refused. Peer replication and repair, liveness,
metrics and internal diagnostics remain available, permitting automatic
recovery when all views agree again. Without a ts-orch cluster identity, startup
admits an immutable one-node topology containing the bound self identity; this
local admission has no external heartbeat lease. Supplying `TS_PEERS_FILE`
always requires a fresh independent manifest, even for a one-node cluster and
in Development. Regional test fixtures own and renew their independent admitted
manifests; tests never disable the production gate.

The peer-authenticated internal `GET /v1/health/topology` returns
`{mismatch,ageSeconds,generation,reason,peers}` with `Cache-Control: no-store`.
`peers` contains actual configured replication links plus startup-bound self.
The native `/metrics` exposes scalar
`terrabase_peer_topology_mismatch` (0 agrees, 1 unknown/drift) and
`terrabase_peer_topology_generation_age_seconds` (-1 if unknown). Generation is
the desired service generation, not a storage format or a membership revision.
The comparison also checks full identities when membership changes within a
service generation. No storage format changes are involved.
