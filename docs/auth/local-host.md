# ts-auth local host

For local development or production in gru, `TS_AUTH_TERRABASE_URL` can address the local TerraBase internal listener.
ts-auth outside gru sets `TS_AUTH_TERRABASE_URL` to gru's internal listener on port 8081 over the private network;
every replica uses the one database in gru. Production startup rejects loopback URLs outside gru.
`TERRABASE_PEER_SECRET` must be gru's TerraBase peer secret outside gru (the existing `gru-1-terrabase-peer-secret`);
it is required with that URL; a missing or malformed value
stops startup. The named TerraBase handles every storage read and write without
other-region fallback.

A gru outage stops sign-in everywhere until step 2: a readable copy per region with a promotable home.
The new-fleet deploy specification in ts-orchestrator supplies the cross-region URL; this PR does not change that specification.

Production hosting and migration status are in [OCI hosting](../oci.md).

| Variable | Example | Meaning |
|---|---|---|
| `TS_AUTH_TERRABASE_URL` | `http://127.0.0.1:8081` | TerraBase internal listener origin. Outside gru in production, set this to gru's private listener on port 8081; loopback is rejected. |

## Secrets as files

Every secret variable (`TS_AUTH_*`, `CLOUDFLARE_*`, `TERRABASE_PEER_SECRET`) also accepts `<NAME>_FILE=/path`: ts-auth reads
the file, trims the trailing newline, and the file wins over `<NAME>`. A host that mounts secrets as files (OCI Vault on gru)
delivers them this way. An unreadable file stops the start, naming only the variable.
