---
status: accepted
date: 2026-08-30
authority: direct workspace-owner instruction
---

# Private protobuf and gRPC protocol

## Decision

Private TBEP uses generated protobuf messages and gRPC over HTTP/2. The schema
of record is the shared engine protocol schema. The implementation uses
prost and tonic. The generated bindings in `terrabase-go` are the only network
client bindings permitted to communicate with TerraBase over private TBEP;
the orchestrator and CLI use them. Public clients call the member's public
HTTP API (D-34). They never call TBEP.

TBEP is not a public binary protocol. Public reactive delivery may use the
documented HTTP snapshot/log format and optional WebSocket transport; its
MessagePack frames are a public result-view representation, not TBEP. Private
protobuf schemas, generated artifacts, and fixtures remain confined to the
control boundary.

TerraBase still owns the transport-independent reactive result-view state
machine. The member serves view attachment, immutable snapshot/log responses,
and optional WebSocket or HTTP delivery through its public API. TBEP remains
the private control protocol for placement, health, drain, administration, and
fault operations; it does not carry customer query or reactive traffic. The
member serves the public HTTP API itself (D-34); no gateway stands between
public clients and members.

## Schema ownership and generation

The checked-in `.proto` file is the single contract authority. Every field and
enum uses a stable numeric identifier. Released identifiers are never reused.
Compatible changes are additive. A semantic break requires a new negotiated
major version.

Protocol generation runs during the build with pinned dependencies and
no system `protoc`. Go bindings are checked in under `terrabase-go` so its
module can build without a separate toolchain. CI regenerates all consumers from the
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

TerraBase trusts a dedicated Database-gateway CA for client certificates. One
certificate must contain exactly one admitted SPIFFE URI SAN in the
`spiffe://terrascale.tech/<environment>/database-gateway/<instance-ulid>`
namespace. The complete URI is the gateway principal and must appear in the
Member's `gateway_principals` allow-list. Common names, DNS SANs, IP SANs,
caller metadata, and the protobuf channel principal cannot authenticate a
gateway. The channel principal must equal the certificate-derived principal.
Certificates with no matching URI SAN, multiple candidate URI SANs, a wrong
trust root, or an unlisted principal fail before engine dispatch.

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

Private protocol and storage compatibility spans only the current release:
no older private client or Member compatibility reader is retained.

## Required evidence

Release evidence includes schema lint and breaking-change checks, reproducible
Generation, digest and drift checks, valid and error vectors, and
live consumer interchange. The live matrix covers mTLS identity, wrong trust
roots, expiry, cancellation, overload, slow streams, restart, version skew,
unknown fields, oversized messages, truncated input, malformed nested values,
and redacted errors. UDS and TCP lanes record their different trust claims.
