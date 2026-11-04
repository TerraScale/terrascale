# Conditional database deletion

Automation should use `ts --json -yes -expected-id <ULID> db delete <name>`
after verifying the database's project and branch lineage. Consent remains required.
The server resolves the name and requires that exact immutable database ID before
deleting it. A same-name replacement fails with `database_identity_mismatch`; an
unsupported server fails with 404.

The CLI uses the identity-conditioned route only. It never falls back to
unconditional deletion. This protects deletion identity; it does not make branch creation, output publication, or reset atomic.
