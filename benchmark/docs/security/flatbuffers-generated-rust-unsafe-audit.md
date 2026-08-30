# FlatBuffers generated Rust unsafe audit

Status: accepted for the exact generated artifact recorded below.

Reviewed: 2026-08-30.

## Exact boundary

- Generated file: `crates/terrabase-tbep/src/generated/fbs/tbep_generated.rs`
- Schema: `crates/terrabase-tbep/schema/tbep.fbs`
- Compiler: `flatc 25.12.19`
- Rust runtime: `flatbuffers 25.12.19`
- Generated file BLAKE3: `7f6a07d18ef1206629450c0e0a464e86dae12fe93b1d19e8fc83a48b039c0024`
- Schema SHA-256: `edf50d62aa7d24f23ee2addd3eb89d1b4e44a15cf5d6fe2ed05e2ec390ba3e53`
- Original generation commit: `407590888f6d60f691d9ef439075264f94906b1a`

No directory, wildcard, other generated binding, or hand-written source is
covered by this exception. The root engine and the `terrabase-tbep` crate keep
their unsafe-code denial. The nested `tbep-fbs` package alone compiles this
artifact with its exactly pinned runtime.

## Audit findings

The generated unsafe operations fall into the patterns emitted by the pinned
Google FlatBuffers compiler:

- scalar reads and writes through `flatbuffers` runtime functions;
- construction and traversal of verified tables and unions;
- aligned FlatBuffers struct casts and byte copies for fixed-size structs; and
- generated unchecked root helpers.

The first three patterns rely on the runtime verifier's bounds, alignment,
offset, depth, and table-count checks. Hand-written network code calls
`size_prefixed_root_as_tbep_envelope_with_opts` with explicit limits before it
uses any generated accessor. The architecture test forbids either generated
unchecked root helper in that receiver. The nested crate does not publish and
the production parent crate does not expose this generated module directly.

The generated unchecked helpers remain in the artifact because `flatc` emits
them. They are outside the accepted receiver path and require an explicit
unsafe call at any Rust call site. Adding such a call is not accepted by this
record.

## Controls

`tests/architecture/forbidden_imports.rs` enforces all of these conditions:

1. only the exact file above may contain unsafe Rust in workspace source;
2. the file must retain the standard generated header;
3. its BLAKE3 digest must match this reviewed artifact;
4. this record must name the path, digest, and compiler version; and
5. the hand-written receiver must not call either unchecked root helper.

Before production integration, the TBEP generation drift gate must separately
bind the artifact to the schema and pinned compiler. Until that gate lands, the
exact digest above detects every generated-file change and forces review.
Hostile receiver tests must cover truncation, trailing data, oversize frames,
malformed offsets, alignment, union tags, protocol version, feature bits, and
the `TBEP` file identifier before semantic field use.

## Re-audit triggers and limits

Any byte change to the generated file, schema change, compiler or runtime
upgrade, new unsafe pattern, new call site, or expansion beyond the exact path
fails the gate and requires a fresh review. This audit accepts only the Rust
memory-safety boundary. It does not qualify schema semantics, cross-language
compatibility, QUIC authentication, performance, deployment, or production
readiness.
