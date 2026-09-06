# Public MessagePack manifest

Status: Accepted. Producer/consumer parity is a release gate.

## Ownership

TerraBase publishes the public reactive contract. The SDK keeps its pinned
language-neutral manifest at
`src/msgpack/manifest/terrascale.sync.v1.msgpack.json` and generates codecs with
`scripts/generate-msgpack-protocol.ts`. The local generator reads that manifest;
it does not establish producer parity by itself. Compare the manifest and
shared vectors with the published TerraBase pin before release.

## Wire contract

The manifest defines envelope layout, tags, directions, operations, errors,
field positions, and allocation bounds. MessagePack carries the envelope and
result structure. Projected document values remain canonical JSON bytes until
the language delivery boundary, with a 1 MiB aggregate document ceiling.

Protocol 1.0 is the first public contract. Decoders require the declared major
and minor version before reading the body. Compatible additions append tags;
existing tags retain their meanings. Regenerate pre-launch fixtures together;
retired pre-launch layouts require no compatibility client.

Before constructing values, the decoder rejects unsupported maps, extensions,
float32, invalid UTF-8, unsafe integers, unknown versions/features/tags/enums,
invalid union variants, trailing bytes, and declared size/depth violations.
Compression never increases a public limit. Operation-specific bounds follow
[ADR 0032](0032-public-protocol-bounds.md).

## Client and qualification

`TerraScaleSyncV1Client` is the single reactive state machine. It applies
snapshots, deltas, resets, authorization changes, route changes, and
backpressure to bounded authorized views. HTTP is the default transport;
WebSocket is explicit opt-in. Both use the published authentication, attach,
checkpoint, reset, and retry contracts.

Generated-file drift, hostile decode tests, shared semantic outcomes, and
independently pinned golden bytes must pass. A runtime encode/decode round trip
cannot replace producer parity or live transport qualification. All four
customer SDKs consume the same public semantics; no separate gateway codec
owns a competing contract.
