---
status: accepted
date: 2026-08-30
authority: direct workspace-owner instruction
---

# Private protobuf and gRPC protocol

## Decision

Private TBEP uses generated protobuf messages and gRPC over HTTP/2. The schema
of record is `crates/terrabase-tbep/proto/terrabase/v1/engine.proto`. Rust uses
prost and tonic. The generated bindings in `terrabase-go` are the only network
client bindings permitted to communicate with TerraBase.

The production `TerraScale.Database` gateway is implemented in Go and uses
`terrabase-go`. Public clients call that gateway. They never call TBEP or a
TerraBase Member.

TerraBase has no public binary protocol, public WebSocket protocol, public
MessagePack codec, private FlatBuffers codec, dual codec, or compatibility
reader for those retired designs. The database remains greenfield, so the
repository deletes those schemas, generated artifacts, fixtures, and
requirements after the protobuf/gRPC replacement has equivalent test
coverage.

TerraBase still owns the transport-independent reactive result-view state
machine. TBEP exposes its bounded session, snapshot, delta, acknowledgement,
resume, and close operations through gRPC. The Go gateway owns any public HTTP
streaming representation outside this repository.

## Schema ownership and generation

The checked-in `.proto` file is the single contract authority. Every field and
enum uses a stable numeric identifier. Released identifiers are never reused.
Compatible changes are additive. A semantic break requires a new negotiated
major version.

Rust generation runs during the crate build with pinned Rust dependencies and
no system `protoc`. Go bindings are checked in under `terrabase-go` so its
module can build without a Rust toolchain. CI regenerates both sides from the
same schema, compares the result, records the schema digest, and fails on
drift. Generated files never become schema authority.

Every RPC has explicit request, response, and nested-value limits. The gRPC
server applies a whole-message ceiling before domain conversion. Domain
validators then bound identifiers, strings, byte fields, collections,
recursion, query work, subscription queues, and native payloads before engine
dispatch. Unsupported protocol versions and required feature bits fail
closed.

## Transport and identity

Production TCP uses TLS 1.3 with mutual workload authentication and private
network reachability. Plaintext TCP is forbidden. The server validates its
client trust roots and the Go client validates its server trust roots and
expected identity. Certificate rotation cannot silently disable peer
verification.

Unix domain sockets are permitted for local development and process-isolated
tests. They are not a public or cross-host binding. Tests may use plaintext
loopback only when the test name and receipt identify it as a non-production
lane.

gRPC deadlines and cancellation propagate to bounded engine operations.
Servers cap concurrent RPCs and outbound stream queues. A slow or disconnected
consumer cannot retain an unbounded task, payload, or engine subscription.
Graceful shutdown stops admission, drains within a fixed deadline, closes
streams with a typed status, and then releases the listener.

## Compatibility and errors

The protobuf package and service names are versioned. The protocol reports a
stable typed error code plus bounded safe details. Go maps those codes to
public client errors without depending on server message text. Unknown enum
values, invalid oneof combinations, absent required semantic fields, and
operation/body mismatches fail before engine access.

The first release has no customer compatibility obligation. Once released,
CI compares the schema with the last release and rejects field removal, type
changes, identifier reuse, service removal, or changed wire meaning inside the
same major.

## Required evidence

Release evidence includes schema lint and breaking-change checks, reproducible
Rust and Go generation, digest and drift checks, valid and error vectors, and
live Rust-to-Go interchange. The live matrix covers mTLS identity, wrong trust
roots, expiry, cancellation, overload, slow streams, restart, version skew,
unknown fields, oversized messages, truncated input, malformed nested values,
and redacted errors. UDS and TCP lanes record their different trust claims.
