---
date: 2026-08-29
authority: ../../../GOAL.md
---

# Service-managed encryption and key lifecycle

## Decision

The first release uses TerraScale-managed encryption only. It does not expose
customer-managed keys, imported keys, arbitrary KMS endpoints, or customer
cipher selection. OCI Vault supplies a regional key-encryption key; the
Orchestrator resolves the canonical airport `RegionCode` to provider
configuration through workload identity and least-privilege IAM. OCI names
and credentials remain inside that adapter.

Each selected Region wraps a random 256-bit per-database data-encryption key.
The catalog stores the regional wrappers with key-version identifiers. A
database is not placed in a Region until its wrapper exists and a member has
successfully opened it. SQLite files, WAL, native backup snapshots and backup
objects, exports, and branches are encrypted under keys derived from the active
database key with HKDF-SHA-256 and domain-separated artifact identifiers.

Authenticated encryption uses AES-256-GCM through the selected AWS-LC
provider. TerraBase does not implement cryptographic primitives. Every
encrypted unit has a unique 96-bit nonce and authenticated metadata covering
the tenant, database, artifact, format, key version, ordinal, and length.
Nonce reuse, invalid bounds, tag failures, and wrong associated data fail
closed before a value is published.

## Rotation and local development

Vault key-encryption keys rotate automatically and on demand. Rotation
rewraps database keys through a durable operation and does not rewrite data.
Database keys rotate on the service schedule, tenant transfer, or a suspected
exposure; new artifacts use the new version and existing artifacts are
rewritten through bounded resumable operations before retirement.

An unwrapped database key lives only in bounded, locked process memory under a
short authorization lease. It is zeroed on eviction and never written to
swap, crash dumps, logs, or source control. Vault or catalog authorization
loss fails closed for data requiring the key.

Local development uses a separate provider that atomically creates one
owner-only 256-bit key file beside the local database. It rejects symlinks and
permissive modes, retains an existing key across restart, and never
regenerates a key for encrypted data. The `local-file` provider is rejected by
production configuration.

## Backup, restore, and deletion

Backup wrappers are distinct from active-storage wrappers. Restore unwraps a
backup key only inside the authorized operation, creates a new database and
key version, and re-encrypts before activation. Deletion removes access and
then deletes live and backup copies after applicable retention and legal-hold
constraints. Destroying every regional wrapper makes the data unrecoverable;
storage objects, indexes, caches, snapshots, and backups are still deleted.
