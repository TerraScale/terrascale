# The `terrascale.sync.v2` conformance kit (SE-05)

Status: implementation record for the SE-05 packet (issue #7),
2026-08-26. Authority: charter PD-2026-08-26-12 D1/D4 (native clients,
no wasm/C-ABI embeddings; SE-05 is the conformance kit),
PD-2026-08-26-13 D1/D3 (bytes-on-wire budgets; trigger events); the SE
wave spec (fifth revision, SE-05); the SE-04 wire spec
(`docs/sync/terrascale-sync-v2.md`). Worktree `terrabasev2-se05`,
branch `fleet/se05-2`, based on `develop` @ `ef72eca`.

This document is the **per-language harness contract**: everything an
SDK implementation needs to run the golden-vector corpus in its own CI,
reproducibly, with no Rust toolchain assumption. The kit itself lives
in `crates/terrabase-sync-conformance/`:

- the **Rust reference harness** — a library and a
  `terrabase-sync-conformance` binary that executes the corpus against
  the engine's delivery core (`terrabase-sync-core`) and emits the
  versioned conformance report;
- the **corpus integrity manifest**
  (`crates/terrabase-sync-conformance/fixtures/manifest.json`) —
  SHA-256 digests over every corpus file, so an SDK's CI proves it is
  testing the exact committed corpus before running anything.

The corpus itself is SE-04's, untouched:
`crates/terrabase-sync-core/vectors/terrascale.sync.v2/`, schema
identity `terrascale.sync.v2/vector-corpus@1`, wire spec
`docs/sync/terrascale-sync-v2.md`. SE-04's in-crate runner remains the
corpus's regeneration and byte-exactness gate; this kit is the
executable contract around it. Both must stay green.

## Why a kit (and what it is not)

PD-2026-08-26-12 D1/D4: SDK sync clients are native-language
implementations of `terrascale.sync.v2` — no wasm, no C ABI embedding.
"Same sync engine" means one wire contract **plus conformance**: every
SDK passes the same golden vectors in its own CI. The kit is exactly
the machinery that makes that checkable and tamper-evident. It is not
an SDK dependency: nothing here compiles into a client. Conformance,
not compilation, is the compatibility guarantee.

Consequences, stated exactly:

- The corpus is data (JSON) so no toolchain assumption crosses
  languages. An SDK's CI vendors the corpus directory and the
  manifest, verifies the digests, runs a native harness implementing
  the step vocabulary below, and emits the report schema below.
- The Rust reference harness is the reference **semantics** of
  "running the corpus" — the arbiter when two harnesses disagree about
  what a vector means. A native harness that makes the checks named
  here, on the same data, is conformance-equivalent.
- SE-09 (TypeScript `@terrascale/sdk`) and SE-10 (TerraScale.DotNet)
  own their harness implementations and their CI wiring; this record
  fixes the contract both implement.

## The corpus contract

One JSON file per category, committed under
`crates/terrabase-sync-core/vectors/terrascale.sync.v2/`. The
categories are closed — a conforming corpus carries exactly one file
per category, and a run over a corpus that misses (or duplicates) a
category fails:

`fresh-session`, `checkpoint-resume`, `reconnect-replay`,
`slow-consumer`, `region-route-change`, `resync-required`,
`malformed-frame-rejection`, `repeated-change-delta`,
`trigger-notification`.

Every file carries this header, and a harness verifies it before any
step runs:

| Field | Required value | Check name |
| --- | --- | --- |
| `schema` | `terrascale.sync.v2/vector-corpus@1` | `corpus_schema_identity` |
| `protocol` | `terrascale.sync.v2` | `corpus_protocol_identity` |
| `protocol_version` | `2` | `corpus_protocol_identity` |
| `category` | one of the nine above | (coverage, run-level) |
| `server` | scope + config + initial journal | builds the delivery core |
| `steps` | the ordered step list | — |

The `server` section names the scope (`scope.tenant`,
`scope.collection`), the core configuration
(`config.batch_byte_budget`, `config.checkpoint_window`), and the
journal committed before the session (`journal_before`). A server-side
harness builds its delivery core from exactly these values.

## The step vocabulary (the harness contract)

A step object carries **at most one executable key**, optionally
alongside a `note` prose annotation (a note-only step executes
nothing). Any other key, or two executable keys, fails the step as
`step_kind_known`. For each executable key, the harness must make the
checks named below — the names are stable wire values of this contract
and appear verbatim in reports.

### `apply` — journal a commit

`{ "sequence": u64, "operation_id": string, "document": string, "put": {field: value} | "delete": true }`

The delivery core applies the commit in journal order.

| Check | Assertion |
| --- | --- |
| `apply_completes` | the commit applies without refusal |

### `client_frame` / `server_frame` — a recorded frame

`{ "wire": hex, "frame": {structured description} }` — both directions
assert identically.

| Check | Assertion |
| --- | --- |
| `frame_description_seals` | the structured description constructs a frame that validates and seals |
| `frame_encode_bytes_exact` | `encode(frame)` equals the recorded `wire` bytes exactly |
| `frame_decode_roundtrip` | `decode(wire)` succeeds and the decoded kind equals the description's |
| `frame_digest_verifies` | the decoded frame's content digest still matches its body |
| `frame_envelope_identity` | correlation, causation, and idempotency key agree with the description |

### `serve_delta` — serve from a checkpoint

`{ "from": checkpoint|null, "correlation": u64, "causation": u64?, "expect_wire": hex|null, "expect_frame": {...}? }`

The serving core answers a session at `from`.

| Check | Assertion |
| --- | --- |
| `serve_completes` | the inputs parse and the serving call returns |
| `serve_frame_bytes_exact` | the served frame's bytes equal `expect_wire` (or both are absent) |
| `serve_frame_matches_description` | when `expect_frame` is present, the served frame encodes identically to it |

### `serve_snapshot` — a full-state answer

Same shape and same three checks as `serve_delta` (`expect_wire` is
required here).

### `serve_trigger` — a trigger notification

`{ "collection", "sequence", "operation_id", "correlation", "causation"?, "expect_wire", "expect_frame" }`

| Check | Assertion |
| --- | --- |
| `serve_completes` | the inputs parse and the trigger frame seals |
| `serve_frame_bytes_exact` | the trigger's bytes equal `expect_wire` |
| `trigger_content_free` | the description's key set is exactly `causation`, `collection`, `correlation`, `kind`, `operation_id`, `storage_sequence` — no document, field, or value key exists (PD-2026-08-26-13 D3) |
| `trigger_within_budget` | the trigger frame fits the written trigger budget (256 bytes) |

### `decode_rejects` — fail-closed decode

`{ "wire": hex, "error": name }`

| Check | Assertion |
| --- | --- |
| `decode_rejects_typed_error` | the bytes are rejected with exactly the recorded typed rejection (`unsupported_version`, `malformed_frame`, `idempotency_pairing`, `invalid_idempotency_key`) |

### `budget` — bytes-on-wire expectations (PD-2026-08-26-13 D1)

`{ "wire": hex, "class": name, "payload_bytes"?, "envelope_overhead_bytes"?, "bytes_per_delivered_change"?, "delta_bytes"?, "snapshot_bytes"? }`

The recount is always from the committed wire bytes, never from the
recorded numbers.

| Check | Assertion |
| --- | --- |
| `budget_frame_decodes` | the recorded wire bytes decode |
| `budget_class_matches` | the frame's class equals the recorded class |
| `budget_within_budget` | wire length fits the class's written budget, except a `delta_batch` flagged `single_commit_oversize` with `commits == 1` |
| `budget_payload_accounting` | when `payload_bytes` is present: the decoded payload length and `wire − payload` overhead equal the recorded values |
| `budget_bytes_per_delivered_change` | when present: `wire length ÷ commits` equals the recorded per-change value (delta batches only, `commits > 0`) |
| `budget_delta_below_snapshot` | when `delta_bytes` and `snapshot_bytes` are present: the delta path stays below the snapshot re-send |

The `trigger-notification` and `repeated-change-delta` vectors carry
these expectations: a trigger within its 256-byte budget with the
content-free key set, and 8 repeated changes to established documents
whose delta batch (recorded as exact numbers in the vector) stays far
below the snapshot of the same state. These are deterministic corpus
values — SE-07/SE-15 own the *measured* budgets against the written
definitions; nothing here is a measurement.

### `note` — prose

`{ "note": string }` — annotation only; no checks, no execution.

## The corpus integrity manifest

Schema `terrascale.sync.v2/corpus-manifest@1`, committed at
`crates/terrabase-sync-conformance/fixtures/manifest.json`:

```json
{
  "schema": "terrascale.sync.v2/corpus-manifest@1",
  "protocol": "terrascale.sync.v2",
  "protocol_version": 2,
  "vectors": [
    {
      "category": "fresh-session",
      "file": "fresh-session.json",
      "sha256": "<64 lowercase hex>",
      "bytes": 6697
    }
  ]
}
```

One entry per corpus file, sorted by file name. Verification fails
closed: unknown schema identities and foreign protocols do not parse;
a file whose digest or size disagrees, a manifest entry without a
file, and a file without an entry are all problems. A harness run
gated on the manifest records `manifest_entry_matches` per vector and
fails the run on any problem.

Regeneration is a deliberate act:
`cargo run -p terrabase-sync-conformance -- --emit-manifest`
(in the crate directory) rewrites the manifest from the live corpus.
Regenerate **only** together with a deliberate corpus regeneration
(SE-04's `regenerate_corpus`); a manifest that regenerates differently
without a deliberate change is a contract break, not noise.

## The conformance report

Schema `terrascale.sync.v2/conformance-report@1`. The reference run
over the committed corpus today: **9/9 vectors, 317/317 checks, status
`pass`** (308 checks without the manifest gate).

```json
{
  "schema": "terrascale.sync.v2/conformance-report@1",
  "protocol": "terrascale.sync.v2",
  "protocol_version": 2,
  "harness": "terrabase-sync-conformance/0.1.0",
  "corpus": "terrascale.sync.v2",
  "summary": {
    "status": "pass",
    "vectors_total": 9, "vectors_passed": 9, "vectors_failed": 0,
    "checks_total": 317, "checks_passed": 317, "checks_failed": 0,
    "problems": []
  },
  "manifest": { "status": "verified" },
  "vectors": [
    {
      "category": "trigger-notification",
      "file": "trigger-notification.json",
      "status": "pass",
      "aborted": false,
      "checks": { "trigger_content_free": 1, "trigger_within_budget": 1, "...": "..." },
      "failures": []
    }
  ]
}
```

Rules the schema carries:

- The `manifest` section appears exactly when a manifest gated the run
  (`{"status": "verified"}` or `{"status": "failed", "problems":
  [...]}`); it is absent otherwise.
- `summary.problems` holds run-level problems (a missing or unexpected
  corpus category, manifest set disagreement). A run passes only when
  every vector passes, the category coverage is exact, and — when
  supplied — the manifest gate verified.
- A failed check **aborts the rest of its vector** (`aborted: true`):
  later steps would only cascade the same root cause. Other vectors
  still run.
- `checks` counts executed checks by name (passed and failed);
  `failures` lists each failure's step index (null for file-level
  checks), check name, and static reason.
- Reports are **deterministic**: no timestamps, no host paths, no
  corpus or payload bytes. The corpus identity is the directory's
  name. Failure details carry step indices, check names, byte counts,
  and static reason text only. Two runs over the same corpus produce
  byte-identical reports — asserted by the kit's own tests.

## Per-language CI recipes (SE-09 / SE-10)

No Rust toolchain is involved. An SDK's CI needs exactly the corpus
directory, the manifest, and this contract:

1. **Vendor** `vectors/terrascale.sync.v2/*.json` and the manifest
   into the SDK repository (a pinned sync of the engine repository's
   committed corpus — the same nine files, digest-verified).
2. **Verify integrity first**: recompute SHA-256 and byte counts of
   every vendored file against the manifest; any mismatch fails the
   run before a vector executes.
3. **Run a native harness** implementing the step vocabulary above
   against the SDK's own codec: every `client_frame`/`server_frame`
   asserts `decode(wire) == frame` and `encode(frame) == wire`; every
   `decode_rejects` asserts the typed rejection; every `budget` step
   recounts from the committed bytes. A server-side harness may also
   replay `serve_*` steps; a client-side harness asserts the frame and
   rejection steps, which are the client contract.
4. **Emit the report** (`terrascale.sync.v2/conformance-report@1`)
   as a CI artifact and fail the build on any failure, problem, or
   gate mismatch. Reports from different harnesses over the same
   corpus are diffable: same schema, same check names, same counts.
5. **A corpus change is a contract change**: when the engine
   repository regenerates the corpus (deliberately), the SDK vendor
   step updates corpus + manifest together, and a mismatch between a
   vendored corpus and its manifest fails closed.

## The engine-side reference run

In this repository the kit is a standalone-workspace crate (like
`terrabase-sync-core` before its integrator wiring): the engine
workspace's checks pick it up when the integrator adds
`crates/terrabase-sync-conformance` to the root `members` list (a
named collision action in the SE-05 packet report). Directly:

```sh
cd crates/terrabase-sync-conformance
cargo test                                   # the kit's own contract suite
cargo run --bin terrabase-sync-conformance -- \
  --manifest fixtures/manifest.json --report conformance-report.json
```

Exit codes: `0` pass, `1` conformance failure, `2` usage or I/O error.
The `--corpus` flag selects a corpus directory (default: the committed
corpus); `--emit-manifest` writes the integrity manifest to stdout.
In CI, the report is the artifact of record for
`terrascale.sync.v2` conformance.

## Non-claims

- No performance, bandwidth, latency, or throughput claim is made or
  implied (PD-2026-08-26-03, PD-2026-08-26-13 D1). The corpus's byte
  counts are deterministic artifact values; SE-07/SE-15 own measured
  budgets and Benchmark Receipts.
- The kit does not qualify any SDK implementation; SE-09/SE-10 do
  that, and their conformance runs are the qualification records.
- The kit adds no protocol behavior: it executes SE-04's contract
  exactly. A corpus regeneration that changes any byte is SE-04's
  deliberate-change discipline, not this kit's.
- Reports are point-in-time records of a corpus run; they carry no
  secrets, no tenant data, and no document bodies by construction.
