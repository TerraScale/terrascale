---
status: superseded
superseded_by: ADR-0019
---

# ADR 0013: Rust server, C#-only client, QUIC/TLS binding

> Superseded on 2026-08-29 by the workspace `GOAL.md` and ADR 0019. Public
> TypeScript and .NET clients use TerraScale APIs. The independent C#/.NET
> `TerraScale.Database` service is the sole QUIC TBEP client. UDS is not a
> supported target binding. ADR 0025 supersedes this record's codec-selection
> clause with generated FlatBuffers. The body remains unchanged as historical
> evidence.

Parent record of charter PD-2026-08-28-02, 2026-08-28, accepted on user
instruction.

Owner: the parent fleet orchestrator, on the workspace owner's direction
that TerraBaseV2 has a Rust server and, for now, only a C#/.NET client,
connected through an efficient protocol.

## Context

Earlier records (PD-2026-08-26-01, PD-2026-08-26-02, PD-2026-08-26-12)
multiplied client languages and made a C# API host a mandatory hop. The
owner reversed that scope: one server process, one client language. The
existing C# HTTP host may remain as a transitional backend adapter but is
not the database server and must not be a required hop on the hot path.

## Decisions

### D-1 — serving boundary

The Rust process is the database server. It owns storage, transactions,
queries, indexes, replication, schema enforcement, Loro-backed CRDT
convergence, cluster membership, and the TerraBase Engine Protocol
(TBEP). A C# HTTP process is not the database server and is not required
on the data hot path.

### D-2 — one client language

C#/.NET is the only supported V2 database and sync client language.
`TerraBase.Protocol` (in `host/`) is the protocol client library and
`TerraScale.DotNet` is the public C# SDK integration point. TypeScript
and all other language clients are out of scope for TerraBaseV2 database
support. The Dashboard's TypeScript use of `@terrascale/sdk` consumes
separately owned customer API contracts and does not extend V2 client
support. This scope decision requires no SDK repository edits now;
existing SDK code and historical delivery evidence remain untouched
until separately authorized follow-up work.

### D-3 — bindings

QUIC with TLS is the supported network binding for C# client to Rust
server and Rust Member to Rust Member communication. UDS remains for
same-host administration and tests. Both bindings carry the same
versioned, typed TBEP schema. Authentication, tenant identity,
certificate rotation, replay refusal, capability negotiation, flow
control, and overload behavior fail closed. C# clients use short-lived
tenant-scoped tokens over TLS 1.3. Member links use mTLS with rotated
identities. QUIC 0-RTT is disabled for mutations; any future read-only
0-RTT path must bind authorization and prove replay safety before
admission.

### D-4 — protocol efficiency

One connection multiplexes requests and independent streaming lanes. The
wire supports batching, cancellation, idempotent retry, backpressure,
reconnect/resume, and bounded frames. Codec, compression, buffer
pooling, copy count, and syscall count are measured choices.
MessagePack stays the codec until a benchmark receipt proves a
replacement is better.

### D-5 — no leadership vocabulary in the protocol

TBEP has no `NotLeader`, master discovery, primary endpoint, or
backup-to-active promotion operation (ADR 0018). A C# client connects to
any active Member and retries the same operation through another Member
without a leader lookup or redirect.

## Consequences

PD-2026-08-26-01 D1/D3, PD-2026-08-26-02 D1's host-as-mandatory-surface
requirement, and PD-2026-08-26-12 D1/D3/D4's multi-SDK clauses are
superseded; those records stand as history. The QUIC client binding, TLS
identity, and remote admission path are not yet qualified — the UDS
binding is the tested surface today, and the gap is recorded honestly
until P4 delivers it.
