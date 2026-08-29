# ADR 0025 public MessagePack manifest — TypeScript half (2026-08-29)

Status: **implemented (TypeScript half only)**. Honest scope below.

This lands gap-sweep row 28's TypeScript half
(`TerraBaseV2/docs/evidence/qualification-gap-sweep.md`): the public
`terrascale.sync.v2.msgpack` WebSocket protocol is now generated from one
checked-in versioned manifest, per GOAL.md ADR 0025 and the ADR 0032 decoded
ceilings. Nothing existed before this change (row 28: "missing").

## What exists now

- **The manifest is the authority**: `src/msgpack/manifest/terrascale.sync.v2.msgpack.json`
  (manifest version 1, protocol `terrascale.sync.v2.msgpack`, major 2 minor 0,
  feature mask 0). It declares the 7-element indexed-array envelope
  `[major, minor, features, tag, correlation, causation, body]`, 19 messages
  with explicit numeric tags (never reused), named structured types, closed
  enums, tagged unions, and per-field bounds. Message names align with the
  engine corpus vocabulary (`TerraBaseV2/crates/terrabase-sync-conformance`):
  `session_setup`, `session_accepted`, `subscription_modify`,
  `subscription_changed`, `checkpoint_ack`, `heartbeat`, `delta_batch`,
  `snapshot_answer`, `trigger`, `resync_required`, `region_route_change`,
  `error`, `mutation_request`, `mutation_ack`, plus the protocol additions the
  GOAL public-transport list requires: `history_expired` (typed expiry),
  `causal_token`, `auth_generation_changed`, `operation_progress`,
  `backpressure`. Bounds mirror ADR 0032 where applicable: 8 MiB per message,
  depth 64, 4 MiB per chunk payload, 128 subscriptions, 16 KiB tokens, 16-byte
  ULIDs, 1024 mutation fields, and the engine corpus per-field ident/detail
  limits.
- **A pinned, dependency-free generator**: `scripts/generate-msgpack-protocol.mjs`.
  It validates the manifest (unique tags/discriminants/enum values, backward
  type references only, bounds within the scan ceilings, maps/extensions/
  float32 forbidden) and deterministically emits the TypeScript half.
  `--check` is the drift gate. Regeneration is byte-identical (no timestamps).
- **Committed generated output**: `src/msgpack/generated/terrascale-sync-v2-msgpack.ts`
  — public types, per-type exact-shape validators (including unknown-property
  rejection), canonical encoders, and the decode pipeline. The header pins the
  manifest and generator SHA-256 digests.
- **A minimal bounded MessagePack runtime**:
  `src/msgpack/runtime/bounded-msgpack.ts` — allocation-bounded structural
  scan (whole-message, string, binary, array, map, extension, float32, depth
  limits; one complete value; no trailing bytes) that runs before any value
  decode, a strict reader (exact array lengths, declared integer ranges with
  bigint u64/i64 semantics, fatal UTF-8, no floats where integers are
  declared), and a canonical writer. Every rejection is one typed
  `TerrascaleMsgpackError`; decode assembles the public value only after every
  field validated.

## Checks (all run 2026-08-29, this checkout)

- `npm run build && node --test test/msgpack-manifest-drift.test.mjs` — 21
  tests: regeneration byte-identical to the committed module, `--check`
  passes, manifest digest pinned in the generated header, determinism across
  runs, and 15 malformed-manifest classes rejected with no output emitted.
- `npm run build && node --test test/msgpack-hostile-decode.test.mjs` — 30
  tests: every strict prefix of a valid message rejected; trailing bytes;
  whole-message over 8 MiB; binary over 4 MiB; string/list/array-bound
  breaches; nesting beyond depth 64; unknown message tags; floats where exact
  integers are declared (version, correlation, counters), negatives and
  >2^32 values in u32 fields (unsafe integers); version skew (majors 0/1/3,
  minor 1); unknown feature bits; envelope and body arity violations (extra
  and duplicate semantic fields); maps; extensions; float32; invalid UTF-8;
  unknown enum values; unknown union discriminants; wrong-length ULIDs; the
  reserved byte 0xc1. All rejections are typed and occur before a public
  value is constructed.
- `npm run build && node --test test/msgpack-corpus-roundtrip.test.mjs` — 5
  tests: every frame description in every vector of the vendored engine
  corpus (`test/fixtures/terrascale.sync.v2/`, read-only) maps onto the
  manifest vocabulary and round-trips encode→decode with deep semantic
  equality (8 corpus kinds, >20 frames); a synthetic sweep round-trips all 19
  manifest messages including every union variant and option branch; the
  exact-shape validator rejects malformed in-memory values; write-side bounds
  reject over-long strings, negative u64s, and >4 MiB payloads; a legal 4 MiB
  snapshot chunk round-trips.
- Repo gates re-run with the new tree: `npm run typecheck` passes,
  `npm test` passes (159/159), `check:forbidden-surface` and
  `check:route-manifest` pass.

Pre-existing, out-of-packet failures observed in this checkout (untouched by
this change; the failures reference the Dashboard repo's OpenAPI snapshot):
`check:openapi-drift` (78/79 routes) and, consequently,
`check:openapi-contract-descriptor` and `check:public-contract-fixture` over
`GET /api/v1/projects/{projectId}/saved-queries/{savedQueryId}`. No path this
packet owns is involved.

## Honest scope — what this is NOT

- **C# bindings do not exist yet.** ADR 0025 requires generated C# contracts
  for MessagePack-CSharp from the same manifest plus C#-built/TypeScript-read
  and TypeScript-built/C#-read conformance; row 28's acceptance needs both
  bindings. The manifest here is language-neutral JSON precisely so the C#
  generator can consume it unchanged.
- **The live protocol is not switched over.** The reactive client still
  speaks the committed native `terrascale.sync.v2` corpus codec
  (`src/sync/v2/`); nothing sends or receives
  `terrascale.sync.v2.msgpack` bytes on a socket yet. Switchover (server
  emission, WebSocket subprotocol registration, resume/cancel paths) is a
  later coordinated phase per the ADR's versioning section.
- **`@msgpack/msgpack` is not used.** ADR 0025 names it for TypeScript, but
  it is not a dependency of this repository, and adding one would touch the
  shared `package.json` outside this packet's owned paths. This packet
  vendors a minimal bounded core with the decoder-limit posture the ADR
  requires instead. Migrating onto `@msgpack/msgpack` (pinned in
  `package.json`, with `useBigInt64` and explicit limits) is a follow-up that
  must re-pass the same three suites.
- **Cross-language conformance and fuzzing are not run.** Structure-aware
  fuzzing, supported-version-skew matrix, fragmentation, and decompression
  lanes remain release-qualification work under ADR 0025's conformance
  section.
- **Performance is unqualified** (shared host; GOAL evidence discipline).

## Regenerating

```sh
node scripts/generate-msgpack-protocol.mjs          # regenerate in place
node scripts/generate-msgpack-protocol.mjs --check  # drift gate (CI)
```

Edit the manifest, never the generated file.
