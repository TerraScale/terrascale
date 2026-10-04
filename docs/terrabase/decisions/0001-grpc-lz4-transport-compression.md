# 0001: LZ4 gRPC transport compression for inter-node traffic

- Status: accepted (human-approved, 2026-10-04)
- Scope: `Features/Replication`

## Context

The repository rules require LZ4 compression for all TerraBase-to-TerraBase gRPC traffic.
The Rust reference (`terrabase-rust/docs/distributed-protocol.md`, "gRPC transport and bounded
framing") instead compresses replication payloads inside a typed application envelope
(`PayloadCodec`, raw LZ4 block) and keeps gRPC transport compression disabled.

## Decision

The C# peer listener registers a custom `lz4` gRPC message encoding
(`Lz4CompressionProvider`, one LZ4 frame per message, NativeCompressions) and uses it as the
server response encoding. Peer clients register the same provider and request it with
`grpc-internal-encoding-request: lz4`.

- Decoded messages are capped at `ReplicationExtensions.MaxMessageBytes` (4 MiB application
  batch plus framing); a larger frame fails instead of inflating.
- The checked-in `replication.proto` is unchanged on the wire (only `csharp_namespace` added), so
  the application envelope and `PayloadCodec` stay available.

## Consequences

- A Rust peer does not negotiate the `lz4` encoding. Interoperating with it requires sending
  identity-encoded messages to it, or adding the encoding there. Not validated yet.
- When `CommittedGroupBatch` lands, payloads already LZ4-compressed in the application envelope
  must not be compressed again; decide then whether those calls disable transport compression.
- `LZ4Stream` in NativeCompressions 0.6.1 omits the LZ4 frame header and cannot decode its own
  output; the provider uses the one-shot `LZ4.Compress`/`LZ4.Decompress` API instead.
