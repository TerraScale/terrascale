# Public MessagePack manifest

Status: Accepted for the shared manifest and bounded TypeScript generation. C#
generation, live transport adoption, and production qualification are pending.

Date: 2026-08-29

## Context

The public reactive protocol needs a versioned, language-neutral contract with
bounded decoding and stable field positions. Private TBEP and TerraBase
replication formats must not leak into public clients. TerraBase owns the
reference semantics and corpus, TerraScale.Database owns the public WebSocket
contract, and the two SDK repositories own their generated bindings.

## Decision

Use `src/msgpack/manifest/terrascale.sync.v2.msgpack.json` as the sole local
authority for the public MessagePack envelope, message tags, fields, unions,
and bounds. `scripts/generate-msgpack-protocol.mjs` emits the TypeScript
binding. The accepted in-repository bounded runtime performs a whole-message
structural scan before value construction, rejects maps, extensions, float32,
unknown tags and versions, unsafe integers, invalid UTF-8, depth/size
violations, and trailing bytes, and returns typed failures.

Tags and field positions are never reused. Native payload codecs remain named
and opaque until the owner-published public result-view contract replaces that
interim implementation. The generated TypeScript module is not a new raw
package entrypoint.

## Consequences and limits

Manifest drift, hostile decode, corpus round-trip, and native-view tests pass in
this checkout. The live session still uses HTTP/JSON and SSE, and no C# binding
or bidirectional cross-language run exists here. The release workflow also
needs a dedicated MessagePack lane. Those are production gaps, not implied
claims by the generated files.

The bounded runtime remains the accepted TypeScript implementation. A future
dependency change would require a new reviewed record and must preserve the
same explicit limits, hostile-input behavior, and performance evidence. No
library-selection decision is open in this ADR.
