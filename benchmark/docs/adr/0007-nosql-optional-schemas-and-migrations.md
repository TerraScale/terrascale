---
status: accepted
---

# NoSQL with optional native schemas and migrations

## Collection modes

TerraBase remains a NoSQL document database. Each collection uses one mode:

| Mode | Contract |
| --- | --- |
| `schema-free` | Documents need no declared application schema. A path's first typed mutation establishes its CRDT kind. |
| `validated` | A versioned schema governs declared paths and permits undeclared paths. |
| `strict` | Every application path and relationship must be declared. Unknown paths fail. |

A schema declares value domain, CRDT kind and conflict policy, required and
optional paths, bounds, relationship shape, index capabilities, and rules that
remain valid under merge. TerraBase never infers a counter, set, flag, or
relationship from a plain JSON scalar or array.

Every mutation names the schema version it was validated against. Every Member
validates the typed mutation before WAL admission and validates materialized
state after merge. A remote schema mismatch fails closed and does not advance
applied progress.

Rules closed under the selected CRDT join remain available during a network
partition. Uniqueness, one-time consume, strict cross-record checks, and other
rules not closed under merge use the coordinated lane or are rejected. Schema
validation never pretends a local check proves a global invariant.

## Schema catalog changes

The schema catalog is versioned database metadata. A catalog update uses the
coordinated lane and names its compatibility class. Members admit mutations
only when they understand the required schema and datatype codec. Rolling
deployment may support a declared old and new version window, but unknown
required behavior fails closed.

Adding an optional path or tightening a bound only for future writes may be an
online compatible change. Changing an established CRDT kind, field identity,
relationship identity, or merge policy is breaking. It requires a new path or
incarnation plus an explicit data migration. Reinterpreting stored bytes in
place is forbidden.

## Native schema migrations

A migration is a versioned, idempotent, resumable plan over native V2 data. It
is unrelated to V1 import. The first release permits bounded declarative
transforms over typed paths, defaults, renames, copies, removals, relationship
changes, and new incarnations. It does not execute arbitrary JavaScript, WASM,
plugins, agents, or user processes inside TerraBase.

The engine runs migration output as ordinary authorized native commits. Each
Partition records the plan ULID and digest, source and target schema, cursor,
causal cut, counts, checksums, failures, resource use, and completion. The
migration is a durable long-running operation under ADR 0028. Retry uses its
stable client-request ULID. It can pause, resume, request cancellation before
cutover, or report partial completion. It cannot roll back already replicated
immutable commits by deleting history.

Cutover occurs only after every selected Region understands the target schema,
required Auto-Indexes are ready, migration verification passes, and the
coordinated catalog decision activates the target version. Failed documents
remain visible under the old admitted version or quarantine according to the
declared plan. The engine never publishes a half-activated schema generation.

Schemas and migrations use named public TerraScale operations through
`TerraScale.Database`. There is no generic schema blob, direct Member route,
or raw migration-code endpoint.
