# TerraBase Engine Protocol 2

Status: accepted target contract, not yet qualified.

Authority: workspace `GOAL.md`, especially PD-2026-08-29-13 and
PD-2026-08-29-19, plus ADR 0019.

Protocol identity: `terrabase.engine.protocol/2`.

The existing version 1 UDS implementation and C# host under this repository
are migration sources. They do not implement this contract.

## Boundary

TBEP is the private process protocol between:

- TerraBaseV2, the Rust database engine; and
- `TerraScale.Database`, the independent C#/.NET 10 ASP.NET Core API.

No public SDK, Dashboard module, MCP tool, browser, CLI, or customer process
speaks TBEP. Public clients use versioned TerraScale HTTPS and reactive
contracts.

The Database API is the only issuer accepted for a `PrincipalContext`.
TerraBase does not accept public bearer tokens, cookies, API keys, OAuth
credentials, or Better Auth sessions over TBEP.

## Transport

TBEP uses QUIC for every process link, including same-host loopback:

- Rust uses the repository-approved QUIC and TLS implementation;
- C# uses `System.Net.Quic` backed by MsQuic;
- TLS 1.3 and mutual workload authentication are mandatory;
- mutations cannot use QUIC 0-RTT;
- certificates and trust bundles rotate without process-wide authority; and
- peer identity, deployment, Region, service role, and protocol audience fail
  closed before an application frame is admitted.

UDS, TCP without QUIC, named pipes, shared memory, an in-process C ABI, and raw
HTTP are not alternate production bindings.

## Contract generation

One versioned schema owns:

- operation and notification registry identities;
- MessagePack field numbers and requiredness;
- maximum encoded sizes;
- error codes;
- compatibility rules;
- Rust types;
- C# types; and
- golden vectors.

The generator emits Rust and C# contracts. Contractless, typeless, reflection
fallback, arbitrary maps, and untyped forwarding are forbidden.

Every generated artifact records the schema digest. Build and release gates
fail if generated output, the registry, and the golden corpus disagree.

## Envelope

Every application frame has a bounded typed header and body:

| Field | Rule |
| --- | --- |
| Protocol major and minor | Unknown major fails closed. Minor compatibility follows the registry. |
| Frame kind | Closed generated registry. Unknown kind fails closed. |
| Correlation identity | Unique within the connection generation. |
| Causation identity | Names the request or event that caused this frame when applicable. |
| Request deadline | Absolute bounded deadline in the negotiated time basis. |
| Idempotency identity | Required for retryable logical mutations and absent where the registry forbids it. |
| Principal context reference | Names a current verified context on the connection. |
| Tenant, Project, database, and Partition scope | Present or inherited only where the registry permits an unambiguous binding. |
| Payload digest | Covers canonical encoded body bytes. |
| Body | Closed generated MessagePack type with a registry-defined maximum. |

Length prefixes, numeric representation, map or array form, extension types,
and canonical ordering come from the generated schema. Do not preserve version
1 encoding merely for byte compatibility.

## QUIC stream classes

One pooled connection multiplexes independent bounded streams:

| Class | Use |
| --- | --- |
| Control | Negotiation, health, context install or refresh, cancellation, drain, and typed errors. |
| Unary | Documents, queries, schema, coordinated operations, and administration. |
| Query stream | Bounded row and projection delivery. |
| Reactive | Subscription setup, result snapshots, patches, reset, checkpoint, and authorization-generation change. |
| Bulk | Import, export, snapshot, backup, restore, and bounded catch-up. |
| Usage | Checkpointed authoritative usage-segment export. |

Stream limits, connection limits, buffer limits, and scheduling participate in
the same tenant and Project resource governance used by HTTP admission. A bulk
stream cannot block an interactive mutation or an authorization refresh.

## Principal context

`TerraScale.Database` creates a short-lived `PrincipalContext` only after it
verifies Dashboard identity and a signed `DatabaseAccessGrant`. The context
binds:

- Database API workload identity and connection generation;
- tenant, Organization, Project, database, and permitted actions;
- principal claims used by database policy;
- policy and authorization generation;
- audience, issue time, expiry, and nonce;
- request or session constraints; and
- the grant digest needed for audit correlation.

TerraBase validates issuer, audience, signature or channel binding, expiry,
generation, scope, and replay rules before use. A context cannot expand its
grant. Expired context fails closed. Long-lived reactive streams refresh before
expiry and reauthorize their subscriptions.

The wire never carries passwords, session secrets, OAuth refresh tokens, raw
API keys, payment data, or the full public access token when a bound digest is
sufficient.

## Operation registry

The registry is closed and purpose-built. Initial families include:

- document read, write, patch, and delete;
- bounded TQL query, explain, cancellation, and result streaming;
- schema and migration administration;
- named atomic operations required by Better Auth and public product flows;
- coordinated compare-and-swap, uniqueness, and bounded transactions;
- reactive subscription and checkpoint operations;
- placement, status, metrics, usage, and database administration;
- snapshot, backup, restore, import, and export; and
- supervision, health, drain, and capability negotiation.

Every operation declares:

- request and response types;
- required context actions;
- idempotency and retry policy;
- durability and consistency options;
- maximum request, response, and streamed bytes;
- cancellation and deadline behavior;
- resource-accounting class;
- audit class;
- allowed QUIC stream class; and
- stable typed errors.

A generic execute, raw query, arbitrary path, arbitrary header, arbitrary JSON,
or passthrough HTTP operation is forbidden.

## Errors and partial outcomes

Errors use a generated, bounded envelope with stable code, safe message,
retryability, request identity, and optional structured details. Raw
exceptions, stack traces, storage paths, peer addresses, credentials,
document content, and undeclared remote fields never cross the boundary.

A timeout after partial durable acceptance is not a generic failure. Mutation
outcomes report the stable idempotency identity, `CommitId` when allocated,
requested acknowledgement, observed durable acknowledgements, and the safe
resolution action.

Cancellation stops unnecessary work but cannot erase a durable commit. A
cancelled query or stream releases its tenant-accounted resources promptly.

## Flow control and overload

All queues are bounded. The Database API and TerraBase enforce:

- maximum concurrent connections and streams by service, tenant, Project, and
  database;
- per-frame and per-result bounds before allocation;
- bounded decompression ratios;
- fair scheduling across workload classes;
- deadlines and cancellation;
- receive-window and send-window limits;
- bulk and snapshot rate limits;
- response and reactive-buffer limits; and
- typed overload with retry guidance.

The protocol does not rely on sticky routing or process-local durable state.
Another Database API instance can retry the same idempotent operation through
another eligible Member.

## Compression

Compression is negotiated by registry identity and stream class. Tiny frames
remain uncompressed. Batches and snapshots may use an approved codec after
decompression-bomb, memory, CPU, and benchmark gates pass. Unknown codecs fail
closed.

Dictionary IDs, schema IDs, field IDs, Partition prefixes, counter deltas, and
batch prefix compression reduce repeated wire data. Compression never changes
canonical identity or payload digests.

## Compatibility

Protocol 2 is a deliberate breaking replacement for version 1. There is no
version 1 to version 2 frame translation in TerraBaseV2.

A rolling upgrade may temporarily run separate versioned listeners or
connection pools only when the deployment contract proves:

- no request crosses schema versions inside one operation;
- current and next versions pass the same semantic golden vectors;
- downgrade and rollback limits are explicit;
- unsupported versions fail before data work; and
- the old listener is removed after the compatibility window.

## Conformance and qualification

Release requires:

- one schema digest across registry, Rust, C#, and vectors;
- bidirectional Rust and C# golden-vector tests;
- property tests for framing, bounds, canonical encoding, and unknown fields;
- parser and decoder fuzzing;
- mTLS identity, rotation, expiry, replay, and wrong-audience denial tests;
- MsQuic connection, stream, cancellation, and restart fault tests;
- slow-reader, slow-writer, decompression, and overload tests;
- tenant and Project isolation tests;
- partial-acknowledgement and idempotent-retry tests;
- reactive reauthorization and context-expiry tests; and
- TerraBase.Benchmark overhead receipts.

Existing version 1 TBEP Proof IDs do not qualify protocol 2.

## Non-claims

This contract does not prove that QUIC is faster than another transport, that
the Database API exists, that protocol 2 is implemented, or that any public
route is released. Those claims require current source and receipts.
