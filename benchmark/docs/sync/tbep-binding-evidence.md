# SE-14 TBEP binding receipt (protocol-doc record): the UDS binding, byte accounting, and the binding-split decision record

No public claim: engineering evidence only. The timing rows below are
local timings on one machine under one run; no latency, throughput, or
comparison statement may be derived from them without a Benchmark
Receipt from the exclusive benchmark lane (charter
PD-2026-08-26-03). The byte-accounting rows are deterministic counts.

- Packet: SE-14 (TerraBase/TerraBaseV2#10), branch `fleet/se14-2`,
  worktree `terrabasev2-se14`.
- Producing suites: `crates/terrabase-tbep/tests/vectors_runner.rs`
  (byte accounting, replayed byte-exact), `tests/uds_characterization.rs`
  (same-host timing characterization), and
  `host/TerraBase.Protocol.Tests/TbepGoldenVectorsTests.cs` (the C# side
  of the same byte accounting).
- Run commands:
  - `cargo test` (crate directory) — corpus, drift, adversarial, UDS
    end-to-end suites.
  - `cargo test --test uds_characterization -- --ignored --nocapture`
    — the characterization rows below.
  - `dotnet run` (from `host/TerraBase.Protocol.Tests`) — the C#
    conformance rows.

## Binding decision record (PD-2026-08-26-02 D2, PD-2026-08-26-11 D1)

- **UDS is the required binding for the local engine/host link**, per
  PD-2026-08-26-11 D1: the engine/host boundary is strictly local, the
  socket file is owner-only (`0600`, asserted by the UDS suite), and no
  authentication surface exists there by design.
- **QUIC with mTLS is deferred** until a networked-engine need is
  recorded (same decision, D1). The deferral keeps the decision record
  item: when a networked link class appears, the binding lands as the
  same MessagePack frame layer over `quinn` ↔ `System.Net.Quic` with
  mTLS, and the UDS-vs-QUIC same-host comparison becomes executable.
  **Affirmation for the only currently-existing link class (local)**:
  UDS, without qualification — there is no competing binding to compare
  against while QUIC is deferred; a measured comparison would compare a
  shipped binding against an absent one. This receipt records the
  shipped binding's method and numbers instead.
- No credentials or keys exist in either binding today: the UDS model
  is filesystem permissions; the mTLS key-handling record is owed when
  the QUIC binding's need is recorded (tracked with the deferral).

## Deterministic byte accounting (measured, exact)

From the golden corpus (`crates/terrabase-tbep/vectors/tbep/`), replayed
byte-exact by both the Rust and the C# runners:

- Fixed envelope cost bound: **35 bytes** over the body (worst-case
  integer widths, bin8 body header) plus the 4-byte stream prefix. A
  minimal control frame measures **19 + 4 bytes** on the wire (small
  integers ride as msgpack fixints); the corpus's per-frame
  `overhead_bytes` rows record every measured value.
- The written budget per class (PD-2026-08-26-13 D1) holds for every
  corpus frame in both languages: control ≤ 256, operation ≤ 1024,
  operation_large/subscription/delivery ≤ 65,536, trigger ≤ 256.
- The content-free trigger notification (PD-2026-08-26-13 D3) measures
  **23 bytes of envelope overhead + body** in its corpus row (27 bytes
  total on the stream for the recorded row) — identifiers only, no
  document data.

## Same-host UDS round-trip characterization (one machine, one run)

Method: one connection, synchronous request/answer, std UnixStream;
200 discarded warm-up rounds; linux, 28 cpus; rows below in
microseconds.

| Row | Rounds | Mean | p50 | p99 | Max |
| --- | ---: | ---: | ---: | ---: | ---: |
| `supervision.health` (minimal control frame) | 10,000 | 82 | 69 | 199 | 5,076 |
| `sync.mutation_submit` (server-ack mutation) | 2,000 | 732 | 614 | 2,404 | 26,091 |

First-round `channel.bind` + `sync.session_setup` (cold scope
construction): 728 µs.

Limits: local timings on one unloaded developer machine, single
connection, no statistical gates, no TerraBase V1 or PostgreSQL
reference run — not a benchmark receipt and not comparable to one. The
mutation rows include the delivery core's journal materialization, not
just transport. The numbers exist to characterize the binding's shape
for engineering decisions (for example, batching choices at the host),
not to state performance.

## Facts asserted by the suites

- The cross-language corpus is green on both sides: decode(bytes) ==
  frame and encode(frame) == bytes for every golden row, Rust
  (`tests/vectors_runner.rs`, 3 tests) and C#
  (`TbepGoldenVectorsTests`, 16 tests).
- Adversarial rows fail closed with the recorded typed failure on both
  sides (`tests/adversarial.rs`, 12 tests; the corpus's
  `adversarial-rejection` category replayed by both runners).
- The UDS end-to-end suite drives every frame family the shipped host
  owns over a real socket, including the `0600` permission, the
  channel-binding gate and its refusals, idempotent mutation retry,
  delta/snapshot/checkpoint flows, the ordered batch + content-free
  trigger pair, supervision shutdown, and the `query.cancel` live
  signal (GAP-Q12) — 7 tests.
- The registry drift contract holds: committed bindings equal
  regeneration byte-for-byte, and every `PB-*` parity reference names a
  row of `parity/inventory.json` (7 tests), including the operation
  registry's parity-mapping column (PD-2026-08-26-14 D1).

## Unrun

- `cargo-deny` license check: the tool remains unavailable in this
  environment; `deny.toml`'s parent-ratified allowlist already covers
  the added Rust dependencies (`serde`, `rmp-serde`: MIT/Apache-2.0;
  `serde_json`: MIT/Apache-2.0 — already in the audited closure).
  MessagePack-CSharp 3.1.8 (C#): MIT; pinned at the current release
  because 3.1.4 carries published NuGet advisories (NU1902/NU1903).
- The repo-root `scripts/check.sh` gate: this packet's crate is a
  standalone workspace (integrator wiring recorded), so the root gate
  does not yet compile it; the crate-local equivalents were run green
  (fmt, clippy `-D warnings`, tests, doc) plus the C# build/test.
- CI (`.forgejo`) regeneration and drift wiring: integrator action.
