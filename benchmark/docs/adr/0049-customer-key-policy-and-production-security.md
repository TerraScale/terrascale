---
date: 2026-09-09
authority: ../../../GOAL.md
supersedes: ADR 0030's service-managed-only customer-key restriction
---

# Customer key policy and production security boundary

## Context

ADR 0030 made service-managed encryption the only first-release key choice.
That restriction is superseded. Customers may require a customer-controlled
key provider for a database and may place encrypted backups in a customer-owned
S3-compatible bucket. This record defines the security boundary and the limits
that must be visible to customers and operators.

The key provider is an adapter boundary. It must authorize an exact tenant,
database, key version, Region, and purpose, and return a short-lived lease for
the data key. TerraBase never accepts a raw key from customer configuration and
never treats a bucket credential as a database-key credential. The current
production capability still reports OCI Vault only; the adapter and policy are
not evidence that a customer KMS integration has shipped or passed release
qualification.

## Decision

Retain the existing envelope format and AES-256-GCM implementation. A
customer-managed provider may replace the service-managed wrapper authority
only after it satisfies the same authenticated metadata, exact-scope, bounded
lease, locked-memory, zeroization, rotation, and fail-closed requirements.
Imported key material, arbitrary KMS endpoints, provider escrow, and customer
cipher selection remain unsupported until separately implemented and qualified.
The current customer-provider admission contract accepts canonical AWS KMS key
ARNs (with a UUID key resource and matching Region); aliases and bare key IDs
are rejected at admission;
the service-managed OCI Vault adapter is a separate path. Azure and other
customer providers remain reserved until their adapters are independently
qualified.

The service-managed OCI path remains available where the customer selects it.
The effective key policy is durable database state, is resolved from the
database owner, and is included in admission, rotation, backup, restore, and
deletion operations. A policy change is not complete until the replacement
provider has opened the required key versions and the relevant backup chain has
been verified.

### Control-plane persistence

The authenticated orchestrator NATS journal may persist the validated BYOK
provider references needed to rebuild an admission or provider client after a
restart: provider, key identifier, region, endpoint, backup bucket, workload
role, and configuration revision. AWS web identity uses the workload token's
subject; external IDs are not accepted in admission, renewal, or host
configuration. It may also persist wrapped database
DEK ciphertext required for restart and recovery. NATS never receives customer
provider credentials, imported key material, unwrapped DEKs, or other plaintext
secrets. NATS TLS, NKeys, and subject permissions are required, but they do not
change the replicated-history, snapshot, export, or operator-disk retention
model; the references and ciphertext therefore remain subject to the journal's
access controls and crypto-shred limitations. For AWS KMS, the endpoint
reference is optional; when omitted, the regional SDK endpoint is used. The
admitted region and canonical key ARN remain mandatory and must match.

The current orchestrator runtime wires one AWS KMS authority tuple per process
from the host workload-identity boundary. It verifies every admitted reference
against that tuple and fails closed on a mismatch; it does not silently reuse
one customer's role or key for another database. Supporting multiple distinct
customer authorities in one process requires a separately approved credential-
source mapping and host-install contract. Until that contract is qualified,
deployments must either use a single admitted customer authority per process or
keep additional customer-provider databases out of production admission.

## Production threat model

Assets are customer documents and indexes, WAL and native backup artifacts,
wrapped data keys, unwrapped key bytes, provider credentials, authorization
leases, and the control records that bind them to a tenant and database.

The trust boundaries are the public API, the TerraBase member, the host
Orchestrator, the customer key provider, and each object-storage provider.
Public requests are untrusted. Members may use only leases delivered through
the authenticated host boundary. The Orchestrator owns OCI workload identity
and service-managed wrappers; it does not gain authority over a customer KMS
outside the provider contract. Backup objects are encrypted before upload, but
object storage still learns bucket, object names, timing, size, and request
metadata.

The model covers credential theft, cross-tenant scope confusion, stale or
replayed leases, stale leadership, lost provider responses, corrupted or
partial backup objects, and a compromised storage account. Scope checks,
authenticated encryption, durable fencing, independent restore validation,
and least-privilege credentials address those cases. A host or provider
operator with simultaneous access to live plaintext and its authorized key is
trusted for that access interval. This design does not promise protection from
an already compromised running member, plaintext already delivered to a
customer, provider-retained copies, traffic metadata, or a customer who grants
broader IAM access than this policy.

## Revocation and lease limits

Key authorization leases are bounded to at most 1,800 seconds. The client
subtracts request time, renews before expiry, and refuses new key use after
expiry or provider refusal. Authorization leases for customer operations are
shorter (at most 30 seconds). These bounds limit continued use after a
revocation is observed; they are not instantaneous revocation guarantees.

Revocation cannot retract plaintext already returned, data already written to
an authorized destination, or immutable provider and filesystem snapshots.
Cached key references drain at their operation boundary, and a process crash
or network partition can delay observation until the applicable lease expires.
Deleting a catalog wrapper is immediate service access revocation, not proof
of physical deletion or crypto-erasure. Provider key deletion, replicas,
escrow, retention, and backup semantics require separate evidence as described
by the crypto-shred operations decision.

## Customer S3 policy

Customer-owned backup storage must use a dedicated bucket or prefix and a
dedicated workload-identity role used only by the backup adapter. Customer
configuration supplies the role reference, never a static access key or secret.
The authenticated host broker issues short-lived provider credentials only for
the admitted environment, tenant, project, database, provider tuple, and
placement revision. The minimum
object policy is:

| Operation | Scope |
| --- | --- |
| `ListBucket` / equivalent listing | The configured bucket and backup prefix only |
| `GetObject`, `HeadObject` | Objects below that prefix only |
| `PutObject` and multipart part upload | Objects below that prefix only |
| Abort multipart upload | Multipart uploads below that prefix only |
| `DeleteObject` | Only when the customer explicitly delegates retention cleanup |

Bucket administration, IAM administration, account access, versioning
administration, KMS administration, unrelated prefixes, and cross-bucket
listing are excluded. Transport must require TLS. Because TerraBase encrypts
artifacts client-side, a customer KMS permission is not needed merely to store
the ciphertext; customer-managed object-storage encryption may be layered on
top, but its availability becomes part of restore readiness.

Provider credentials are obtained from the workload-identity boundary, not
recovered from NATS or a durable customer-credential record. Credentials and
workload tokens are never written to NATS, backup objects, logs, metrics, URLs,
or customer-visible error text. Policy revocation prevents new credential
leases; already-issued provider credentials remain subject to their provider
expiry. Credential-issuance or provider denial stops new uploads and causes
backup health to become degraded. The policy is a
required configuration and review target; no customer S3 IAM integration is
declared production-qualified by this ADR alone.

## Restore tradeoffs

Each selected destination must contain a complete independently restorable
chain. OCI and Tigris provider redundancy can survive loss of one object store,
but it does not survive loss of the key provider needed to unwrap the chain.
A customer bucket adds control and residency options while also adding
credential expiry, endpoint availability, lifecycle/versioning behavior,
egress cost, and restore dependence on the customer's IAM policy.

Restore always creates a new database identity and key version, verifies the
manifest, chain, authenticated bytes, and frontier before publication, and
must obtain a fresh authorized lease. A restore cannot proceed when the
customer has revoked the key, removed the required objects, retained only an
incomplete prefix, or imposed an object-storage policy that denies reads.
Re-encryption during restore reduces exposure of the source wrapper but does
not make an unavailable source key recoverable. Restore drills must exercise
each selected provider independently, credential rotation, key revocation,
partial uploads, retention/versioned objects, and loss of the other provider.

## Consequences and release gate

ADR 0030's service-managed-only restriction is no longer the product policy,
but the customer-provider path remains a design and integration boundary until
its implementation, real provider IAM behavior, revocation measurements, and
independent restore evidence are qualified. Documentation, API capability
advertising, and production admission must use the capability status rather
than infer support from the adapter type or local tests.
