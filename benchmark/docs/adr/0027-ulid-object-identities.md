---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# ULID object identities and fixed-width storage

## Decision

Every TerraBase-owned entity identity except a Region is a ULID. This includes
cluster, tenant, Project, database, collection instance, document, document
incarnation, relationship edge, Member, replica, logical Partition, schema
catalog entry, index instance, view plan, subscription, commit, snapshot,
backup, restore, long-running operation, client request, correlation, and
causation identities.

A Region has no ULID. Its internal and public identity is a strict uppercase
three-letter airport `RegionCode`. Provider-specific Region names and resource
identifiers are typed Orchestrator adapter configuration and never enter
TerraBase keys, placement state, protocols, checkpoints, or customer receipts.

TerraBase does not mint UUIDs, GUIDs, integer entity IDs, or arbitrary string
entity IDs. The coordinated greenfield break deletes old identity formats and
compatibility readers; there is no customer-data migration.

The following are not entity IDs and retain their domain-appropriate type:

- collection names, field paths, and public route names;
- causal counters, mutation ordinals, message sequences, versions, epochs,
  bucket numbers, offsets, lengths, and quantities;
- BLAKE3 content digests, range fingerprints, schema digests, and definition digests;
  and
- external-provider references such as Stripe or identity-provider values,
  which remain typed boundary data and never become TerraBase storage keys.

A causal dot remains `(ReplicaUlid, ReplicaEpoch, OriginCounter)`. A mutation
tag remains `(CommitUlid, MutationOrdinal)`. These tuples are causal coordinates,
not alternate entity-ID formats. A field mutation does not receive a separate
ULID merely to make the tag globally unique.

## Canonical representation

The normative value is the 128-bit ULID defined by the ULID specification:

- 48-bit Unix-millisecond component followed by 80 bits of entropy;
- fixed 16-byte unsigned big-endian binary representation; and
- fixed 26-character uppercase Crockford Base32 text representation.

Private TBEP protobuf fields carry ULIDs as exactly 16 binary bytes. Public
REST, logs, operator displays, and SQL literals use the canonical
26-character uppercase text. Decoders reject overflow, incorrect length,
ambiguous characters, non-canonical text, and non-16-byte binary values. One
semantic value therefore has one accepted binary form and one accepted text
form.

The timestamp component is metadata only. It is never trusted for causality,
authorization, conflict resolution, expiry, durability, record retention, or
proof of creation time. ADR 0022 uses it only to reject a client-request ULID
outside the accepted age and future-skew window. That rejection does not prove
freshness. HLC, durable causal state, signed policy, and explicit server
timestamps govern the corresponding contracts.

## Generation

Every generation authority uses a cryptographically secure monotonic ULID
generator. Within one process and millisecond, the 80-bit entropy component is
incremented so generated values remain strictly increasing. Generator state
survives neither as a correctness nor uniqueness dependency: fresh
cryptographic entropy and the full 128-bit value keep identities independent
across processes and Regions. Entropy overflow waits for a later millisecond
instead of wrapping.

Caller-supplied TerraBase entity IDs and database mutation idempotency IDs must
already be valid canonical ULIDs. Retry uses the same client-request ULID and
request digest. An API never silently replaces a malformed or duplicated ID.

## Storage and sharding

Storage keys embed the 16-byte binary value directly. Equality lookup, cache
keys, WAL records, indexes, manifests, and protocol registries do not retain a
second string copy. Comparisons use unsigned lexicographic byte order, which is
the ULID chronological order, and may use fixed-width or vectorized comparison
where benchmarks justify it.

ULID time locality is useful for ordered key-range shards, but the timestamp
prefix alone must not dictate a shard boundary or create a hot Partition.
Under D-28, explicit key ranges split and move ownership without changing the
entity identity. Queries that request chronological creation order use an
explicit indexed time field or the validated ULID timestamp projection; they
do not assume that one timestamp-prefix range is the only shard.

Content-derived objects have both identities and digests. For example, an
Auto-Index instance has an `AutoIndexUlid`, while its canonical definition has
an `AutoIndexDefinitionDigest`. Equal digests may converge to one catalog
decision, but a digest is never accepted as an entity ID. The same separation
applies to view plans, schemas, snapshots, chunks, and artifacts.

## Libraries and evidence

Rust uses the `ulid` crate behind a TerraScale-owned wrapper. Go bindings use a
small repository-owned conversion layer over the fixed 16-byte protobuf field.
Dependencies are pinned in their owning lockfiles. The wrappers fix
cryptographically secure monotonic generation, strict parsing, 16-byte
conversion, canonical text, equality, comparison, and serialization.
Application code cannot select alternate formats or random sources.

One cross-language corpus covers the ULID specification vectors, minimum and
maximum values, same-millisecond monotonic generation, entropy overflow,
invalid and ambiguous text, binary/text round trips, byte ordering, protobuf
representation, storage-key ordering, and concurrent generation in every
supported runtime. Dependency upgrades must pass this corpus and the identity
lookup, insert, index, WAL, and protocol benchmarks.

A separate Region boundary corpus accepts only registered uppercase airport
`RegionCode` values. It proves that engine state, TBEP, public APIs, SDKs,
checkpoints, placement contracts, logs, and customer receipts never substitute
an OCI Region name or `SJC` for `SFO`.
