# ts-auth off Fly

A host without Fly's machine environment (no `FLY_*` variables) names its TerraBase with one setting:

| Variable | Example | Meaning |
|---|---|---|
| `TS_AUTH_TERRABASE_URL` | `http://127.0.0.1:8081` | Origin of the TerraBase internal listener that holds ts-auth's storage. Used when set, in any environment. |

- With the URL set, that one TerraBase takes every read and write: no Fly DNS, no other-region fallback.
- `TERRABASE_PEER_SECRET` is required with the URL, as on Fly; a missing or malformed value stops the start.
- Without the URL and without `FLY_APP_NAME`, only Development stores in the loopback TerraBase; Production refuses to start.
- `ConfigurationTests.Production_off_fly_starts_against_the_terrabase_its_url_names` starts both binaries in Production without
  `FLY_*` and waits for `/healthz`. `/readyz` needs a reachable backup bucket: a production TerraBase opens its catalog only after reading it back.

## Secrets as files

Every secret variable (`TS_AUTH_*`, `CLOUDFLARE_*`, `TERRABASE_PEER_SECRET`) also accepts `<NAME>_FILE=/path`: ts-auth reads
the file, trims the trailing newline, and the file wins over `<NAME>`. A host that mounts secrets as files (OCI Vault on gru)
delivers them this way. An unreadable file stops the start, naming only the variable.
