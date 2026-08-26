# TerraBaseV2 language

This glossary names the concepts used to compare TerraBaseV2 with TerraBase
V1 and to describe evidence for a multi-region database. It contains terms,
not implementation instructions.

## Parity and evidence

**V1 Parity Item**:
A capability or externally observable contract that TerraBase V1 exposes and
TerraBaseV2 must account for. A parity item may be implemented, deliberately
omitted with an accepted reason, or still unresolved.
_Avoid_: feature, checkbox, port

**Parity Matrix**:
The versioned record that maps each V1 Parity Item to V1 evidence, V2
ownership and status, contract semantics, Proof IDs, impact, and risk.
_Avoid_: feature list, coverage table

**Proof ID**:
A stable identifier for an artifact that supports a claim, such as a test,
review, migration record, or benchmark result.
_Avoid_: link, screenshot, confidence

**Acknowledgement Level**:
The strongest state a region or member has confirmed for a replicated record.
The level is part of the public contract and is distinct from local receipt.
_Avoid_: consistency mode, delivery status

**Recovery Receipt**:
A secret-safe record showing what recovery scenario ran, which exact source
and data identity it used, what durability result it produced, and what limits
remain.
_Avoid_: restore log, backup proof

**Benchmark Receipt**:
A secret-safe record of a comparable performance run, including exact source,
workload, environment, statistical result, validity, and baseline relation.
_Avoid_: speed result, timing, benchmark number

**Claim**:
A statement about parity, correctness, durability, multi-region behavior, or
performance that requires a matching Proof ID before it is presented as fact.
_Avoid_: assertion, promise, guarantee

## Topology and replication

**Region**:
A named failure and latency domain that participates in a TerraBaseV2
deployment.
_Avoid_: zone, site, cluster

**Member**:
A database participant identified within a Region and the deployment's
membership view.
_Avoid_: node, replica, server

**Convergence**:
The condition in which members have reached the contract-defined state for a
replicated operation and the associated acknowledgement level is observable.
_Avoid_: eventual consistency, sync, caught up

**Replication Receipt**:
A secret-safe record that proves a replicated operation's acknowledgement,
member observations, and convergence outcome for a defined scenario.
_Avoid_: replication log, sync result

**Failure Domain**:
A set of resources whose correlated loss is treated as one failure in a
qualification scenario.
_Avoid_: availability zone, outage bucket

## Sharding

**Shard**:
The unit of data distribution and ownership: a set of buckets owned by
exactly one Member as one owned data range in the TopologyView, with one
ownership epoch, replicated as a unit under ADR-0004. A shard is a range
and its stable identity, never a separate database or process.
_Avoid_: shard database, node (a Member is the participant; a shard is
data), partition (a shard's bucket set may be non-contiguous),
replica set (replication follows ADR-0004's range ownership, not
per-shard election)

**Bucket**:
The permanent routing identity of a document, derived from its Shard Key
hash into the fixed [0, 2^20) space. A bucket belongs to exactly one
shard at any epoch; it is the unit of migration and of bucket-scoped
storage accounting. Its value is a pure function of the shard key and
never changes.
_Avoid_: slot, vbucket, chunk (migration moves whole buckets; there is
no smaller chunk), range (a range is a set of buckets — saying "range"
for one bucket blurs the Bucket Range term), hash (the bucket is the
reduced value, not the hash itself)

**Shard Key**:
The document attribute(s) whose hash determines the document's Bucket;
the document identity, lowercased, by default. Declared per collection
through a typed operation; changing a collection's shard key is a
migration-scale decision, never a silent reroute.
_Avoid_: partition key, routing key (routing is the two-step lookup
shard key -> bucket -> shard; calling the key "routing" hides the bucket
step), primary key (the identity is the default shard key, not the
definition)

**Bucket Range**:
One entry of the bucket-range table: a contiguous [start, end) interval
of buckets mapped to one Shard, such that the table partitions
[0, 2^20) exactly with adjacent-same-shard entries merged.
_Avoid_: shard range (the range belongs to the table, not the shard's
identity; a shard may own several disjoint ranges), slot range

**Bucket Migration**:
The explicit, idempotent, restartable engine operation that moves one
Bucket from a source Shard to a destination Shard through the replication
plane, ending in a bucket-range ownership transfer with raised epochs.
Its progress is observable (source, destination, resolved records).
_Avoid_: resharding (the Platform-level activity of many migrations plus
table changes; a single migration is the engine operation), rebalance
(a Platform policy decision, not an engine operation), move chunk

## Sync engine and engine protocol

**Sync Engine**:
The single state-machine core that drives client sessions, transitions,
checkpoints, acknowledgements, and delta delivery. One engine owns the
server delivery side; clients implement the Sync Protocol natively in
their own language (PD-2026-08-26-12).
_Avoid_: replication layer, client cache, poller

**Sync Session**:
A resumable client conversation with the Sync Engine, identified by a
Sync Checkpoint and carried over Sync Protocol frames.
_Avoid_: WebSocket connection, changes subscription

**Sync Protocol**:
The versioned client-channel wire vocabulary (terrascale.sync.v2) spoken
between client Sync Sessions and the serving host.
_Avoid_: sync engine, TBEP, wire format

**Sync Frame**:
One typed, versioned envelope unit of the Sync Protocol or TBEP, carrying
correlation and causation identity, an idempotency key where mutation is
possible, and a content digest.
_Avoid_: message, packet, event

**Change Event Backbone**:
The engine's public, journal-backed change-event surface that replays
ordered change events from any checkpoint to authorized consumers.
_Avoid_: event bus, notification center, changes API

**Change Notification**:
The small, typed, content-free trigger event the Sync Engine emits on
commit publication to tell a Sync Session or backbone consumer that new
change data is available; the consumer then pulls delta batches from
its checkpoint (PD-2026-08-26-13). It never carries document data and
never blocks a commit.
_Avoid_: push update, invalidation message, wake-up ping, polling

**Sync Checkpoint**:
The durable resumption identity of a Sync Session, from which
unacknowledged delivery resumes after reconnect.
_Avoid_: cursor, offset (a ReplicationCursor is a member catch-up concept)

**TerraBase Engine Protocol (TBEP)**:
The custom process-to-process wire protocol between the Rust engine
process and C# hosts: typed MessagePack frames over Unix-domain sockets
(same host) or QUIC with mTLS (networked). It is not the client Sync
Protocol.
_Avoid_: C ABI boundary, gRPC service, internal API

**Operations Studio**:
The database's own Vinext operations UI for documents, queries, indexes,
families, topology, and live views — the V2 successor to V1 Studio as
the parity-reference consumer.
_Avoid_: dashboard, admin panel, Studio SPA

**Live View**:
A studio page region updated by change-event-backbone delivery through
the API host's subscription endpoints rather than polling.
_Avoid_: Sync Session (a client-channel concept), live query, stream

## Boundaries and clients

**Platform Adapter**:
The approved typed integration through which Platform clients access public
database capabilities. It carries authorization, tenant isolation,
confirmation, audit, and secret-handling rules at the owned seam.
_Avoid_: SDK bridge, HTTP proxy, generic gateway

**Protocol Adapter**:
A typed adapter for an approved public client or wire contract. It does not
grant access to private control-plane operations or a generic HTTP escape
hatch.
_Avoid_: endpoint, transport shim, API wrapper

**Engine**:
The database-owned behavior for documents, transactions, queries, indexes,
families, streaming, replication, backup, import, storage, and recovery.
_Avoid_: backend, service, server layer

**Control Plane**:
The Platform-owned concerns of provisioning, tenant onboarding, billing,
routing, lifecycle, and external authentication policy.
_Avoid_: admin API, management engine

**Public Seam**:
A named interface at which a capability can be implemented and verified
without exposing private implementation details.
_Avoid_: module boundary, endpoint surface

## Query language

**TQL**:
TerraBaseV2's canonical query language: an SQL-familiar,
document-native surface for schema-optional documents, informed by
V1's RQL, that parses to a typed, versioned AST with no arbitrary
code execution in the language.
_Avoid_: query syntax, SQL dialect, RQL (that is V1's language), TerraQuery

**TQL AST**:
The typed, versioned parse artifact every TQL text lowers to against
the query seam: a closed node set carrying source spans and the TQL
Grammar Version, with validated identifiers, resolved aliases, and
parameter binding slots — never unvalidated strings or executable
code.
_Avoid_: parse tree, query object, syntax tree

**TQL Grammar Version**:
The `tql/MAJOR.MINOR` identity carried by every parsed TQL artifact —
parse input, AST, serialized frames, stored queries, vectors, and
error payloads. Unknown or missing versions fail closed; additive
grammar changes bump the minor and breaking changes bump the major.
_Avoid_: language version, dialect tag, schema version (a Document
Schema concept)

## Indexing and query planning

**Auto-Index**:
An index the Query Optimizer creates, widens, supersedes, or retires
automatically to serve dynamic queries, named deterministically from the
query shape under the reserved `Auto/` namespace and owned by the
engine, never by a user operation. Explicit indexes take precedence
where equivalent; Auto-Index lifecycle changes never weaken durability
or correctness.
_Avoid_: dynamic index (a query is dynamic; an index is auto),
automatic index, ad-hoc index, managed index

**Query Optimizer**:
The engine component that selects the serving index for every dynamic
query — choosing an explicit index when one equivalently serves,
otherwise selecting or creating an Auto-Index — and records its
reasoning in an Index Selection Record. It does not name the
storage-level match planning inside one index evaluation.
_Avoid_: query planner (reserved for the AST-to-plan stage),
query engine, index chooser, Corax optimizer (a V1 storage-level term)

**Index Selection Record**:
The bounded, recorded outcome of one selection: the candidates
considered, each candidate's verdict, the tie-break rules applied, the
chosen index or creation event, and the staleness basis of the answer.
A query served by a worse-than-available index must carry its reason
here. It is engine state queryable through a typed operation, not a log
line and not a debug flag.
_Avoid_: explain plan, query plan, debug explain, selection log

## Data and migration

**Logical Migration**:
A V1-to-V2 data transformation whose input and output are checked against a
manifest, without claiming that V1 files can be opened directly by V2.
_Avoid_: file compatibility, in-place upgrade

**Document Schema**:
An optional, typed, versioned validation contract declared per collection
and enforced on write. Schema-free documents remain valid V2 behavior;
adopting a schema never changes a collection's stored model.
_Avoid_: table definition, JSON schema, model

**Schema Version**:
The recorded version identity of a Document Schema under which a
collection's documents are validated.
_Avoid_: schema revision (a Data Family concept), format version

**Schema Migration**:
An explicit, idempotent engine contract that moves a collection between
Schema Versions with manifest, checksum, count, and rollback-limit
evidence. Distinct from Logical Migration.
_Avoid_: Logical Migration, upgrade script, reindex

**Storage Format**:
The durable representation governed by V2's compatibility and recovery
contract.
_Avoid_: file layout, serialization detail

**Recovery Boundary**:
The point at which a recovery scenario must report durability and data-loss
limits before any result can support a release claim.
_Avoid_: restore point, checkpoint

**Data Family**:
A V1-named group of related data capabilities whose parity obligations are
tracked together while each public contract remains explicit.
_Avoid_: collection type, feature bucket

## Evidence language

**Current**:
The exact source identity or artifact selected for a claim, recorded without
implying that the claim has passed.
_Avoid_: latest, stable

**Qualified**:
Supported by the required evidence for its stated scenario, scope, and
assumptions.
_Avoid_: production-ready, proven everywhere

**Unresolved**:
Known work or a decision that has no accepted disposition yet.
_Avoid_: future enhancement, maybe
