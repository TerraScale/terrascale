# Agent databases: create latency and live proof in gru (2026-11-04)

TerraBase and ts-auth `b7036f1cc` serving in gru (image revision labels). Client: `benchmark/scripts/agent-db/create_latency.py`
run on a gru host against `https://api.terrascale.tech`, one throwaway account (Pro allowance, deleted afterwards).
Each create is `POST /v1/projects/{project}/databases` with `ttlSeconds: 600`, timed end to end over HTTPS, followed by a
delete. One warm-up repeat of 30, then 3 measured repeats of 30.

| repeat | creates | p50 ms | p95 ms | min ms | max ms |
|---:|---:|---:|---:|---:|---:|
| 1 | 30 | 78.4 | 193.5 | 68.4 | 268.1 |
| 2 | 30 | 70.7 | 94.4 | 63.7 | 118.3 |
| 3 | 30 | 65.1 | 88.8 | 58.2 | 103.8 |

Baseline before the change (same client and host, no TTL, 20 creates): p50 62.3 ms, p95 69.1 ms. From delta over the
WAN the same create measured p50 205.8 ms, of which about 70 to 80 ms is network.

Live proof (`benchmark/scripts/agent-db/live_proof.py`, same account):

| check | result |
|---|---|
| fork now (`/branches`, ttlSeconds 3600) | 201 in 236 ms; rows equal the source |
| fork at a point in time (`/point-in-time/restore`) | 201 in 1,372 ms; only rows committed before pointInTime |
| create with ttlSeconds 60 | 201, expiresAt set; deleted 5.9 s after expiresAt |
| scoped key (read) minted by the project key | 201; reads its database |
| scoped key on another database | 404 unknown_database |
| scoped key write | 403 forbidden_scope |
| scoped key minting a key | 403 |
| clear TTL | 200, expiresAt null |

On the Free (hobby) plan the fork answers 409 `database_limit` (1 database): TTL databases and forks count against the
plan's database limit.
