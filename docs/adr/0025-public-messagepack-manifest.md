# Public MessagePack manifest

Status: Accepted.

Date: 2026-08-29, revised 2026-08-30.

TerraScale.Database publishes the public reactive contract. The SDK keeps its pinned
language-neutral manifest at
`src/msgpack/manifest/terrascale.sync.v1.msgpack.json` and generates codecs with
`scripts/generate-msgpack-protocol.ts`. The local generator reads that manifest;
it does not establish producer parity by itself. Compare the manifest and
shared vectors with the published producer pin before release.

The public reactive protocol needs one versioned contract that TypeScript and
Go can consume without copying field layouts into handwritten codecs. Frames
must carry authorized query results and opaque checkpoints. Private storage,
replication, and causal structures are not public SDK contracts.

The manifest defines envelope layout, tags, directions, operations, errors,
field positions, and allocation bounds. MessagePack carries the envelope and
result structure. Projected document values remain canonical JSON bytes until
the language delivery boundary, with a 1 MiB aggregate document ceiling.

`src/msgpack/manifest/terrascale.sync.v2.msgpack.json` is the sole protocol
authority. It declares the indexed envelope, message tags and directions,
result operations, closed errors, field positions, and allocation bounds.

`scripts/generate-msgpack-protocol.mjs` validates that manifest and emits the
TypeScript types, validators, encoder, and decoder. Generated files are never
edited by hand. INT-3 must make the Database API's Go codec check the same JSON
manifest.

Version 2.1 is the first publishable wire contract. The pre-launch 2.0 files
were never released through npm or served by a live transport, and their tag
assignments are withdrawn. Both sides fail closed across this boundary because
the envelope decoder requires an exact major and minor version before it reads
the body. The 2.1 tag assignments are stable; compatible additions append new
tags instead of reassigning an existing tag or direction.

The decoder scans one complete MessagePack value before constructing a public
value. It rejects maps, extensions, float32, invalid UTF-8, unsafe integers,
unknown versions, features, tags, enum values, union variants, trailing bytes,
and all declared size or depth violations.

`TerraScaleSyncV2Client` is the current reactive state machine. It accepts the
generated server frames and applies snapshots, deltas, resets, authorization
changes, route changes, and backpressure instructions to authorized result
views. SDK-2 owns the WebSocket and HTTP + MessagePack transports after INT-3
publishes their routes, authentication, subprotocol negotiation, fallback
request and response shapes, and retry semantics.

The Loro/native-v2 client remains temporarily in source so its deletion can be
reviewed in a dependent mechanical stage. It is not an alternate current
contract and may not receive new behavior.

## Consequences

Changing any frame requires changing the manifest and regenerating the
TypeScript module. The drift check rejects manual generated-file edits. The
semantic corpus round-trips every message kind, and hostile byte tests gate the
decoder. Committed 2.1 golden bytes independently pin every message kind and
hostile boundary for the later Go implementation. A second handwritten frame
codec or compatibility client is not allowed after the deletion stages land.
