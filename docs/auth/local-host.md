# ts-auth local host

A local process addresses its TerraBase internal listener with `TS_AUTH_TERRABASE_URL`.
`TERRABASE_PEER_SECRET` is required with that URL; a missing or malformed value
stops startup. The named TerraBase handles every storage read and write without
other-region fallback.

Production hosting and migration status are in [OCI hosting](../oci.md).

| Variable | Example | Meaning |
|---|---|---|
| `TS_AUTH_TERRABASE_URL` | `http://127.0.0.1:8081` | Origin of the TerraBase internal listener that holds ts-auth's storage. Used when set, in any environment. |

## Secrets as files

Every secret variable (`TS_AUTH_*`, `CLOUDFLARE_*`, `TERRABASE_PEER_SECRET`) also accepts `<NAME>_FILE=/path`: ts-auth reads
the file, trims the trailing newline, and the file wins over `<NAME>`. A host that mounts secrets as files (OCI Vault on gru)
delivers them this way. An unreadable file stops the start, naming only the variable.
