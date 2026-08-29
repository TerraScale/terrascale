---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Generated FlatBuffers and measured performance gates

## Context

The workspace owner selected FlatBuffers to replace MessagePack in the current
TBEP and public reactive binary protocols. Earlier ADRs selected or retained
MessagePack. Their superseded bodies remain historical evidence, but they no
longer define the active codec.

The codec change does not itself prove speed. TerraBaseV2 also needs an
unambiguous point at which database comparisons become valid, a measurable
PostgreSQL target, a substantially faster RavenDB target, and performance gates
for multi-Region replication and reactive sync.

## Decision

### Schema ownership and generated bindings

TBEP and the public reactive protocol each own one checked-in, versioned `.fbs`
schema. The build pins the exact `flatc` version and flags. CI regenerates the
TBEP Rust/C# bindings and public C#/TypeScript bindings and fails on any diff.
Generated files are never edited as the schema authority.

Every root declares a protocol family, major and minor version, required
feature bits, operation identity, and typed body union. Field identifiers are
never reused. A breaking shape creates a new major. Unknown required features,
unknown unions, invalid enum values, and unsupported majors fail closed.

TBEP carries native mutation, CRDT, snapshot, usage, and repair bytes only as
explicitly typed, length-bounded opaque vectors. FlatBuffers does not replace
the canonical native codecs and hashes owned by ADR 0022.

### Decode safety and conformance

A receiver authenticates the transport and checks the outer frame and declared
uncompressed bounds before allocating. It then verifies FlatBuffers file and
schema identity, offsets, tables, vectors, strings, unions, nesting, and
configured object-count limits before any generated accessor reads a field.
Compression cannot bypass either the compressed or decompressed bound.

The release corpus includes:

- pinned-generation and clean-regeneration tests;
- independently specified golden values and byte vectors;
- Rust-built/C#-read, C#-built/Rust-read, and public TypeScript cross-reads;
- same-major forward and backward schema-evolution tests;
- cancellation, batching, retry, reconnect, and protocol-version skew;
- truncated, corrupt, oversized, invalid-offset, invalid-vector,
  unknown-union, excessive-nesting, and decompression-bomb cases; and
- mutation and structure-aware fuzzing in every decoder.

A round trip generated entirely by one implementation is not independent
conformance evidence.

### Benchmark sequencing and targets

Codec microbenchmarks measure build, verify, traverse, bytes, copies,
allocations, CPU, and p50/p95/p99 latency across representative control, query,
mutation, result, replication, and sync frames in each language. End-to-end
receipts measure the same protocol through QUIC and the public WebSocket path.

Decision-grade database comparison for a workload starts only after the exact
candidate passes that workload's functional, correctness, durability,
recovery, security, resource-bound, and conformance gates. Earlier local or
isolated timings are development inputs and cannot support a product claim.

Each required fair PostgreSQL workload must be `v2-faster` or
`no-improvement`, with primary metrics statistically non-inferior under
TerraBase.Benchmark's five-percent band. Each required fair RavenDB workload
must be `v2-faster` and show either a throughput confidence interval of at least
two times or p95 and p99 latency confidence intervals of at most one half.
Other primary metrics remain non-inferior. Invalid, failed, or `inconclusive`
receipts do not pass, and an aggregate cannot hide a failing workload.

RavenDB is an external performance reference only. This target creates no V1
parity, compatibility, migration, data-format, query, client, or feature
requirement.

### Multi-Region replication and sync

Replication and reactive-sync benchmarks run only after their deterministic
correctness and fault suites pass. The matrix covers selected one-, two-,
three-, and twenty-Region topologies, concurrent writes, bounded WAN faults,
backlog catch-up, anti-entropy, snapshot bootstrap, Region loss and rejoin,
cross-Region client reconnect, reset, authorization change, slow consumers,
fan-out, and noisy tenants.

Receipts retain exact stored, applied, verified, and authorized-view-visible
frontiers with latency, throughput, convergence time, bytes, CPU, allocations,
copies, backlog, reset rate, and foreground interference. A change passes only
when it has no statistically significant regression against the best approved
valid baseline for the same topology and fault shape. Builders keep optimizing
measured bottlenecks without weakening correctness, durability, authorization,
verification, isolation, fairness, backpressure, or recovery.

## Consequences

- Active TBEP and public reactive contracts use FlatBuffers. Superseded ADR
  bodies and historical evidence may still name MessagePack.
- Existing runtime code and receipts remain implementation evidence, not proof
  that the new contract is delivered.
- `flatc`, schemas, generated bindings, independent vectors, hostile-input
  tests, and benchmark workload families become release inputs.
- TerraBase.Benchmark remains the sole authority for PostgreSQL, RavenDB, codec,
  replication, and sync performance claims.

## Rejected shortcuts

- A zero-copy label is not a benchmark result.
- A decoder that reads generated accessors before structural verification is
  not accepted.
- A fast incorrect or incompletely durable workload is not comparable.
- One aggregate score cannot hide a slow workload, tail regression, failed
  Region shape, or tenant-isolation failure.
- Weakening durability, batching semantics, response validation, authorization,
  repair, or recovery to improve a result is forbidden.
