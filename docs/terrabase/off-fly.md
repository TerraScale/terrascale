# TerraBase off Fly

Without Fly's machine environment (`FLY_REGION`, `FLY_MACHINE_ID` and `FLY_APP_NAME` all unset) a node is a cell of its own,
named `local`, in any environment:

- The internal listener (8081) binds loopback; the client (8080) and metrics (9091) listeners bind every interface outside
  Development.
- No peer discovery through `.internal` DNS.
- Outside Development, without `TERRABASE_AUTH_ISSUER`/`TERRABASE_AUTH_AUDIENCE`, it trusts the platform pair
  (`https://auth.terrascale.tech/terrascale`, `https://api.terrascale.tech`), as `ts-terrabase` does. Set the pair to trust
  another ts-auth.
- Continuous backup is still required outside Development: the Tigris bucket variables and `TERRABASE_MASTER_KEY`
  ([backup.md](backup.md)). The catalog opens only after the node reads its anchor from that bucket: with an unreachable bucket the node is live but never seeds the platform databases.

## Secrets as files

Every `TERRABASE_*` variable and each Tigris bucket variable (`AWS_*`, `BUCKET_NAME`) also accepts `<NAME>_FILE=/path`:
TerraBase reads the file, trims the trailing newline, and the file wins over `<NAME>`. A host that mounts secrets as files
(OCI Vault on gru) delivers them this way. An unreadable file stops the start, naming only the variable.
