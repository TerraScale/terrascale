---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Service-managed encryption and key lifecycle

## Decision

The first production release uses TerraScale-managed encryption only. It does
not expose bring-your-own-key, hold-your-own-key, customer key import, arbitrary
KMS endpoints, or customer-controlled cipher selection. A later customer need
requires a separate public contract and architecture decision.

OCI Vault supplies regional AES-256 master encryption keys protected by
FIPS 140-2 Level 3 HSMs. An Orchestrator-owned OCI adapter resolves the
canonical airport `RegionCode` to Vault provider configuration. TerraBase uses
that adapter through OCI workload identity, private service-gateway routes, and
least-privilege IAM policies. OCI names and OCIDs remain inside the adapter. No
static OCI credential or plaintext master key exists in a repository, image,
environment variable, log, backup, or database.

TerraBase applies its own authenticated encryption before bytes reach local
NVMe, OCI Block Volume, OCI Object Storage, or backup storage. Provider-side
encryption remains enabled but is not the database format's security boundary.

## Key hierarchy

Each environment and canonical airport `RegionCode` has separate
service-managed key-encryption keys for database data, backup data, checkpoint
tokens, and infrastructure secrets. Production and non-production keys never
overlap.

Each database has a random 256-bit database key version. The catalog stores a
copy wrapped by the current regional OCI Vault key in every selected active
Region and eligible backup Region. Adding a Region cannot expose data until its
wrapper exists and a qualified Member proves decryption through its regional
Vault. The catalog keys every wrapper by `RegionCode` and key-version ULID. OCI
key OCIDs exist only in Orchestrator adapter configuration and internal
deployment evidence, not TerraBase state or customer receipts.

Artifact keys are derived with HKDF-SHA-256 from the active database key and a
domain-separated input containing the artifact kind, artifact ULID, format
major, and key version. WAL files, immutable segments, index files, snapshots,
backup artifacts, manifests, and large value chunks therefore use distinct
keys. Keys for one database or artifact cannot decrypt another.

## Authenticated encryption

The data cipher is AES-256-GCM through AWS-LC FIPS. Production builds enable
the FIPS feature and exclude the non-FIPS provider feature. Startup fails
closed unless the linked provider reports an operational FIPS module.
TerraScale does not implement AES, GCM, HKDF, or random generation.

Every encrypted unit has one unique derived key and a 96-bit nonce formed from
its bounded chunk or record ordinal. A writer rotates the artifact before the
ordinal space can repeat. WAL recovery scans the durable tail before allocating
the next ordinal and never rewrites an encrypted record under the same artifact
key. Nonce reuse is a corruption event and fails closed.

Authenticated additional data contains the environment, tenant ULID, database
ULID, artifact kind and ULID, format major, key version, ordinal, logical
length, compression algorithm, and canonical plaintext digest where the owning
format requires it. Decryption verifies the tag and all bounds before native
decode or decompression publishes a value.

Encryption metadata contains no customer plaintext. Logs and receipts may name
key-version ULIDs and provider-reference digests but never key bytes, wrapped
key material, nonces paired with plaintext, or decrypted customer data.

## Rotation and caching

OCI Vault key-encryption keys rotate automatically every ninety days and on
demand after a suspected compromise. Rotation creates a new OCI key version and
rewraps database keys through a durable ADR 0028 operation. It does not rewrite
all database data.

Database keys rotate yearly, on tenant transfer, and after an incident that may
have exposed a plaintext key. New artifacts use the new version immediately.
Existing artifacts are re-encrypted through bounded resumable operations before
the old version can retire.

An unwrapped database key may remain only in locked process memory under a
thirty-minute authorization lease. The cache is bounded, tenant-accounted, zeroed
on eviction, and never written to crash dumps or swap. A Member renews the lease
through regional Vault and current catalog authorization. After lease expiry,
Vault unavailability fails closed for data requiring that key. Already durable
encrypted bytes remain intact.

## Local development keys

Local development uses a separate provider that persists one generated
256-bit database key in an owner-only regular file under the database engine
location. The provider creates the file atomically, rejects symbolic links,
rejects group or other permissions, preserves an existing key across restart,
and never regenerates a key for existing encrypted data. Its provider identity
is `local-file` and production configuration rejects it.

Local key files, their contents, and derived keys never enter logs, Studio
responses, backups, benchmark artifacts, or source control. Losing the file
makes the matching local encrypted database unrecoverable. Local keys do not
satisfy production encryption or FIPS evidence.

## Backup, restore, deletion, and disaster recovery

Backup artifacts use backup-purpose wrappers distinct from active storage.
Restore unwraps the backup key only inside the authorized restore operation,
mints a new active `ClusterUlid` and database-key version, and re-encrypts the
restored state before activation.

Deletion first removes access and schedules every live and backup copy under
the retention contract. After the last applicable retention or legal-hold pin
expires, TerraScale destroys every regional wrapper and database-key version.
Crypto-erasure is evidence in addition to physical deletion, not a reason to
skip deletion of storage objects, indexes, caches, snapshots, or backups.

Disaster-recovery runbooks prove that surviving regional or backup wrappers can
recover data without the lost Region. They also prove that losing all required
wrappers makes encrypted data unrecoverable and cannot be bypassed with a static
root key.

## Qualification

Release evidence covers known-answer vectors, cross-process decrypt, wrong-key
and wrong-AAD refusal, tampered tags, truncated ciphertext, nonce uniqueness,
crash and replay at every WAL boundary, regional Vault outage, cache expiry,
key rotation, re-encryption, Region addition and removal, restore, legal hold,
crypto-erasure, memory and core-dump inspection, IAM denial, and secret-redacted
receipts.

Dependency and image scans verify the selected cryptographic provider and FIPS
mode. Performance receipts measure encryption throughput, latency, allocation,
and re-encryption interference without permitting plaintext or keys in benchmark
artifacts.
