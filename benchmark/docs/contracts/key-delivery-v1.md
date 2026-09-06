# Host key delivery version 1

The native host orchestrator owns OCI workload identity, Vault endpoints,
regional ciphertext wrappers, key-version authorization and revocation.
TerraBase requests leases over an owner-private Unix socket; neither process
reads an unwrapped database key from local configuration or disk.

Both peers authenticate the other's kernel UID. The socket is mode 0600 and
its parent is mode 0700, owned by the orchestrator service user. Both services
run under that configured UID. Ancestor directories must not be symlinks or
writable by another user. The consumer rejects missing or unsafe sockets,
unknown versions and malformed replies without exposing provider responses.

Every frame starts with an unsigned 32-bit big-endian payload length, bounded
to 2048 bytes. Integers inside the payload are also big-endian.

A lease request contains `TBKL`, byte `1`, then seven UTF-8 strings, each
prefixed by its unsigned 16-bit byte length. Every string is 1–128 bytes.
Their order is environment, tenant ULID, project ULID, database ULID,
database-key-version ULID, airport RegionCode, purpose. ULIDs use their
canonical 26-byte uppercase form. Purpose is `data`, `backup` or `checkpoint`.
The full tuple selects one authorized regional wrapper; a matching version
under another tuple must never be accepted.

A successful reply is exactly 68 payload bytes:

| Bytes | Value |
| --- | --- |
| 0–3 | `TBKL` |
| 4 | Version `1` |
| 5 | Status `0` |
| 6–9 | Lease seconds, 1–1800 |
| 10–35 | Requested canonical key-version ULID |
| 36–67 | Unwrapped 256-bit database key |

A refusal is six payload bytes: `TBKL`, version `1`, status `1`. It contains
no provider error, key material, ciphertext or resource identifier. Clients
reject any other shape. TerraBase reads key bytes directly into locked,
non-dumpable anonymous memory, subtracts request elapsed time from the
received lease and zeroes memory on drop. Connection read/write deadlines
are 25 seconds; the orchestrator limits its Vault request to 20 seconds. An expired lease cannot create or use an artifact cipher.

The durable orchestrator catalog retains wrapped old versions for authorized
reads during rotation. Rotation never changes the bytes behind an existing
key version. Restart reloads wrappers and obtains fresh Vault authorization;
missing or revoked records fail closed. A newly provisioned database is not
admitted until every selected Region has a wrapper and a member successfully
opens the requested key version. This transport's existence does not itself
qualify provider IAM, database storage encryption or release readiness.
