# Transport security

Every public SQL surface is encrypted and authenticated (PIVOT D22).

## Where TLS terminates

The ts-orch edge on each region host terminates TLS with its ACME (Let's Encrypt) certificates for the
`terrascale.tech` hostnames and hands the decrypted stream to TerraBase over loopback.

| Surface | Public endpoint | Plaintext |
| --- | --- | --- |
| PostgreSQL wire | `pg.terrascale.tech:5432` (`sslmode=require`, or PG17 `sslnegotiation=direct` with ALPN `postgresql`) | refused at startup: `28000 TLS is required: use sslmode=require`, before any password is requested |
| REST | `https://api.terrascale.tech`, `https://<region>.api.terrascale.tech` | `GET` redirects to HTTPS; other methods answer `400 Use HTTPS` |
| WebSocket SQL | `wss://` on the REST hosts | the upgrade is redirected, never accepted |

Region links between edges are mutual TLS over the NetBird mesh.

## TerraBase refuses connections that bypass the edge

TerraBase's client (`8080`) and PostgreSQL (`6432`) listeners serve credentialed traffic only from a
loopback peer, the co-located edge. Any other peer, such as a host on the mesh, is refused:

- PostgreSQL: `SSLRequest` answers `N`, then startup fails with `28000 TLS is required: use sslmode=require`
  (fatal). No `AuthenticationCleartextPassword` is sent, so the client never sends its key. A
  `CancelRequest` carries no credential and is still honored.
- REST and WebSocket: `403 tls_required`, `TLS is required: use https://api.terrascale.tech`. Probe
  endpoints (`/healthz`, `/readyz`, backup health) still answer for ts-orch readiness and ingress.

Only the edge's PROXY v2 header and `X-Forwarded-For` name the real client for IP allowlists.

Service-to-service MagicOnion RPC (the dashboard) is not edge traffic: it arrives on the private internal listener over the NetBird mesh with the peer credential, and TerraBase serves its in-process REST replay from any mesh address. The same calls on the public client listener without the edge stay refused.

## Authentication

PostgreSQL clients authenticate with an API key as the password (any user name). The key is sent only
inside TLS. A wrong, unknown or foreign key answers `28P01 password authentication failed`; REST answers
`401`. SCRAM-SHA-256 is not offered: the keys are ts-auth credentials, and SCRAM would need ts-auth to
publish per-key verifiers, while channel binding cannot cross the edge's TLS termination.

Verify the certificate with stock libpq:

```sh
psql "host=pg.terrascale.tech port=5432 dbname=<project>/<database> user=any sslmode=verify-full sslrootcert=system"
```

## Proof

- ts-orchestrator `internal/edge` tests for the edge's TLS listeners.


A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
