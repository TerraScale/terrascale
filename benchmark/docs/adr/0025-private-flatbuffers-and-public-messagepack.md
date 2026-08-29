---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Private FlatBuffers and public MessagePack protocols

## Decision

TerraBase uses two binary protocol families with different trust boundaries:

- private TBEP between the Rust engine and `TerraScale.Database` uses generated
  FlatBuffers over QUIC with TLS 1.3 and mutual workload authentication; and
- the public reactive WebSocket between `TerraScale.Database` and the C# and
  TypeScript SDKs uses generated MessagePack over TLS.

FlatBuffers is never exposed to a public browser or SDK. MessagePack is never
used on TBEP. The Dashboard and public SDKs never connect to a TerraBase Member.
There is no JSON WebSocket, dual codec, raw frame escape hatch, or generic TBEP
operation.

## Private TBEP

TBEP owns one checked-in root `.fbs` schema and may split reusable definitions
into checked-in includes. The root schema declares the `TBEP` file identifier
and one envelope table containing the protocol major and minor, required
feature bits, operation discriminant, correlation and causation ULIDs, and a
typed body union.

Every table field has an explicit contiguous numeric ID. Every union member has
an explicit discriminant. IDs and discriminants are never reused. CI runs
`flatc --conform` against the last released schema, regenerates Rust and C#
bindings with the pinned compiler and flags, and fails on a diff. Generated
files are never schema authority.

A QUIC stream carries standard size-prefixed FlatBuffers. One prefix names one
complete buffer; no second transport length prefix is added. The stream and
operation registry impose a global frame ceiling and smaller per-operation
limits. A receiver authenticates the peer, validates the prefix and declared
compressed and uncompressed lengths, and then runs a configured structural
verifier before any generated accessor reads a field. Rust uses only the safe
verified root APIs for network data with explicit depth and table-count limits.
C# runs the generated root verifier with explicit maximum depth and table
count, alignment checking, and string-termination checking before access. A
generated semantic validator then enforces every operation's vector, string,
object-count, enum, union, and native-payload bounds before dispatch. Unchecked
access is forbidden on network input.

TerraBase-owned ULIDs use the fixed 16-byte representation from ADR 0027.
Native commits, CRDT state, causal contexts, snapshots, and hashes remain
canonical ADR 0022 payloads carried in typed, length-bounded byte vectors.
FlatBuffers does not redefine or hash those values.

Control frames are not compressed. Large opaque payloads may negotiate
application-level Zstandard only when a decision-grade codec receipt proves
lower end-to-end latency or bytes without exceeding CPU, allocation, or tail
latency guardrails. The receiver checks compressed size, decompressed size,
expansion ratio, and payload hash through a bounded sink before native decode.

## Public reactive MessagePack

The public WebSocket subprotocol is `terrascale.sync.v2.msgpack`. One binary
WebSocket message contains one complete MessagePack envelope. Application code
does not depend on WebSocket frame boundaries inside a possibly fragmented
message.

One checked-in language-neutral protocol manifest owns message tags, indexed
field positions, types, required features, bounds, and version rules. A pinned
generator emits:

- annotated, source-generated C# contracts for MessagePack-CSharp; and
- TypeScript encoders, decoders, validators, and public types for
  `@msgpack/msgpack`.

Messages use indexed arrays and explicit numeric message tags. Contractless,
typeless, dynamic object, reflection-driven, and arbitrary extension decoding
are forbidden. C# decoding uses `MessagePackSecurity.UntrustedData`, generated
formatters, and explicit depth and collection limits. TypeScript configures
explicit string, binary, array, map, extension, and whole-message limits, then
runs a generated allocation-bounded structural scan before library decode and
an exact-shape validator before constructing a public value. The scan enforces
nesting, collection counts, declared lengths, extension tags, and one complete
value without trailing bytes. The libraries' permissive defaults are not
protocol limits.

ULIDs are 16-byte binary fields. Unsigned 64-bit counters and sequences are
`bigint` in TypeScript with `useBigInt64: true` and bounded unsigned integers in
C#. Map keys,
collections, text, binary payloads, and nesting have per-message limits.
Unknown message tags, invalid enum values, extra required behavior, unsafe
integers, duplicate semantic fields, and trailing bytes fail closed.

Public MessagePack compression is disabled initially. MessagePack-CSharp's
integrated LZ4 extensions and stateful WebSocket compression are not portable
public protocol dependencies. A future compression capability requires a
versioned cross-language contract, secret-mixing review, decompression bounds,
and an end-to-end receipt.

## Versioning and the coordinated break

The greenfield cutover performs one coordinated breaking replacement. It
removes the old TBEP codec, public sync generations, JSON WebSocket frames,
generated artifacts, fixtures, and compatibility readers. No customer data or
released V2 client requires migration, dual write, or dual protocol support.

After the first stable protocol is published, compatible changes are
append-only within the current major. A field position, field ID, union tag,
message tag, scalar meaning, or default is never reused or reinterpreted.
Breaking meaning requires a negotiated new major. Unknown required features or
unsupported majors fail closed; optional additive fields may be ignored only
where the manifest explicitly permits it.

## Conformance and qualification

The release corpus contains independently specified semantic values and valid
byte fixtures. It proves Rust-built/C#-read and C#-built/Rust-read TBEP, plus
C#-built/TypeScript-read and TypeScript-built/C#-read public messages. A
single-implementation round trip is not independent evidence.

Cross-language producers need not emit byte-identical FlatBuffers or
MessagePack unless a separately specified canonical profile requires it.
Readers must agree on the semantic value. ADR 0022 canonical payloads and
hashes remain byte-identical.

Qualification covers clean regeneration, schema conformance, supported-version
skew, cancellation, batching, retry, reconnect, fragmentation, truncated and
trailing data, malformed offsets and lengths, excessive nesting and
collections, unknown unions and message tags, unsafe integers, decompression
bombs, allocation bounds, mutation fuzzing, and structure-aware fuzzing.

The compiler, generator, Google FlatBuffers runtimes, MessagePack-CSharp,
`@msgpack/msgpack`, Rust `quinn`, and .NET `System.Net.Quic`/MsQuic bindings are
pinned independently in their owning lock files. Textual version numbers need
not match across ecosystems; an upgrade passes the complete corpus and codec
benchmarks before adoption.
