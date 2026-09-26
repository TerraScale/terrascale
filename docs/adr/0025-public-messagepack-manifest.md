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

The manifest is the sole protocol authority. It declares the indexed
envelope, message tags and directions, result operations, closed errors, field
positions, and allocation bounds.

MessagePack owns only the envelope and reactive control structure. Document
bodies inside frames are raw TerraScale-canonical JSON bytes. The gateway
passes those bytes through without transcoding. The production client must
perform exactly one native `JSON.parse` for each document body after decoding
the envelope. A MessagePack-encoded document body and double JSON decoding are
both forbidden. There is no JSON WebSocket variant.

`scripts/generate-msgpack-protocol.ts` validates that manifest and emits the
TypeScript types, validators, encoder, and decoder into
`src/msgpack/generated/`. Generated files are never edited by hand. INT-3 must
make the Database API's Go codec check the same JSON manifest.

Version 1.0 is the first public wire contract. Both sides fail closed across
version boundaries because the envelope decoder requires an exact major and
minor version before it reads the body. Tag assignments are stable; compatible
additions append new tags instead of reassigning an existing tag or direction.

The decoder scans one complete MessagePack value before constructing a public
value. It rejects maps, extensions, float32, invalid UTF-8, unsafe integers,
unknown versions, features, tags, enum values, union variants, trailing bytes,
and all declared size or depth violations.

`TerraScaleSyncV1Client` is the current reactive state machine. It accepts the
generated server frames and applies snapshots, deltas, resets, authorization
changes, route changes, and backpressure instructions to authorized result
views. HTTP attach, snapshot, and long-poll log reads are the default
transport; WebSocket and server-sent events are explicit opt-in upgrades.

## Consequences

Changing any frame requires changing the manifest and regenerating the
TypeScript module. The drift check rejects manual generated-file edits. The
semantic corpus round-trips every message kind, and hostile byte tests gate the
decoder. Committed 1.0 golden bytes pin every message kind and hostile boundary
for the later Go implementation independently of runtime round trips. A second
handwritten frame codec or compatibility client is not allowed.
