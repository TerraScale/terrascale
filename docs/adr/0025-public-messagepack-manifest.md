# Public MessagePack manifest

Status: Accepted.

Date: 2026-08-29, revised 2026-08-30.

## Context

The public reactive protocol needs one versioned contract that TypeScript and
Go can consume without copying field layouts into handwritten codecs. Frames
must carry authorized query results and opaque checkpoints. Private storage,
replication, and causal structures are not public SDK contracts.

## Decision

`src/msgpack/manifest/terrascale.sync.v2.msgpack.json` is the sole protocol
authority. It declares the indexed envelope, message tags and directions,
result operations, closed errors, field positions, and allocation bounds.
MessagePack owns that envelope and operation structure. Per D-30, projected
document values remain opaque TerraScale-canonical JSON byte strings. The
SDK enforces the 1 MiB document ceiling over their aggregate size in a result
item and does not transcode them into nested MessagePack values. SDK-2 parses
each delivered value at most once with the browser's native `JSON.parse`.

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

Before launch, all TypeScript, Go, and Rust peers cross an explicit pre-launch
all-peer reset boundary. The old v1 and native-v2 artifacts were never
released or used by a live peer and must not be resumed. The first
publishable protocol is MessagePack 2.1. Stale 2.0 and v1 artifacts fail
closed at that boundary.
Launch limits are D-25's PITR retention of Free 1 day, Starter 7 days, Pro 30
days, and Scale 35 days; databases per organization of Free 1, Starter 5,
Pro 25, and Scale soft-unlimited; and a maximum document size of 1 MiB.

The decoder scans one complete MessagePack value before constructing a public
value. It rejects maps, extensions, float32, invalid UTF-8, unsafe integers,
unknown versions, features, tags, enum values, union variants, trailing bytes,
and all declared size or depth violations.

`TerraScaleSyncV2Client` is the only reactive state machine. It accepts the
generated server frames and applies snapshots, deltas, resets, authorization
changes, route changes, and backpressure instructions to authorized result
views. SDK-2 owns the WebSocket and HTTP + MessagePack transports after INT-3
publishes their routes, authentication, subprotocol negotiation, fallback
request and response shapes, and retry semantics.

## Consequences

Changing any frame requires changing the manifest and regenerating the
TypeScript module. The drift check rejects manual generated-file edits. The
semantic corpus round-trips every message kind, and hostile byte tests gate the
decoder. Committed 2.1 golden bytes independently pin every message kind and
hostile boundary for the later Go implementation. A second handwritten frame
codec or compatibility client is not allowed.
