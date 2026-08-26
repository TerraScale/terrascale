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

## Sync engine and engine protocol

**Sync Engine**:
The single state-machine core that drives client sessions, transitions,
checkpoints, acknowledgements, and delta delivery. One engine serves the
server delivery side and the embedded SDK cores.
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
