# Preview database branches

`ts db branch create <name> --from <source> [--ttl 7d]` snapshots the source
using its observed immutable ID and an atomic fork TTL. The default is seven
days; durations are whole seconds from 60s to 365d. `--expected-id <ULID>`
requires a previously observed source identity. No unguarded fallback occurs.

`ts db branch list` lists only databases with `branchedFrom`, including expiry.
`ts db branch delete <name> --yes [--expected-id <ULID>]` verifies branch lineage
and conditionally deletes the immutable ID. It cannot delete a non-branch.

`ts db branch url <name> [--expected-id <ULID>]` requires `TERRASCALE_API_KEY`.
It issues a database-scoped read/write key and prints a secret Postgres URL,
using TLS verification and the immutable database ID. The key lifetime is the
smaller of seven days and remaining branch TTL. Under 60s remaining fails.
JSON output is `{connectionUrl, expiresAt}`. Never log this output.

All commands accept `--json` and `--project`. Create/list/delete also accept
user sign-in. The original `ts db branch <source> <target>` remains available
for a permanent copy. Source forks never inherit TTL. No new storage is used.
