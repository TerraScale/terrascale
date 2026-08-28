---
status: accepted
---

# ADR 0005: Workspace stack policy, the TerraBase Engine Protocol, and the unified sync engine

Parent record of workspace decisions PD-2026-08-26-01 and
PD-2026-08-26-02, 2026-08-26, accepted on user instruction.

Owner: the parent fleet orchestrator, on the workspace charter amendments
PD-2026-08-26-01 (stack policy; ticket waves authorized) and
PD-2026-08-26-02 (custom C#↔Rust wire protocol) recorded in the
workspace charter, and on the 2026-08-26 base-engine and interop surveys
they cite.

## Context

The workspace owner fixed the program's language and stack boundaries:
Rust is reserved for the database engine core; API hosts, CLIs, and
adjacent tooling are C#/.NET; dashboards are Vinext (React RSC on the
VitePlus `vp` toolchain); NATS is used where its semantics fit; and
communication between C# and Rust must use a custom wire protocol (QUIC,
MessagePack, or better). This ADR records those decisions at repository
level; the workspace `AGENTS.md` carries the workspace-wide policy, and
the workspace charter carries the amending decision records.

## Decisions

### D-1 — language split: Rust engine core only; C# external surfaces

The Rust scope is the engine core: storage, WAL, engine, families, query,
index, streaming, replication and the sync core, the event backbone, and
their interop artifacts. Public API hosts and CLIs are C#/.NET, hosted in
this repository under the `host/` and `cli/` project families. The W3-3
Rust protocol and Platform adapter seams remain the engine-internal typed
contract; they are not replaced. Rejected alternatives: an in-process
C-ABI-embedded C# host (replaced by D-2's process separation) and a Rust
HTTP host (contradicts the owner's policy that non-engine surfaces are
C#).

### D-2 — the TerraBase Engine Protocol (TBEP)

The engine runs as its own process. Every process-to-process C#↔Rust link
speaks TBEP: typed, versioned frames — operations from the closed
`PublicOperation` set, event-backbone subscription streams, sync-delivery
frames, topology and admin reads, and supervision frames — encoded in
MessagePack from a shared generated schema (Rust `rmp-serde` against C#
`MessagePack-CSharp`; contractless and typeless modes disabled; unknown
major versions fail closed). Transport bindings: Unix-domain sockets for
same-host links and QUIC with mTLS (`quinn` against `System.Net.Quic`)
for networked links. The binding split is verified by measurement in the
protocol ticket (SE-14), not assumed. The C ABI remains only for
in-process SDK embeddings of the sync core (PD-2026-08-26-02 D3); it is
not a service boundary.

### D-3 — hybrid base engine: Loro for the client channel, the accepted W4 plane for member replication

The client/sync channel runs one engine — Loro (MIT), adopted through the
SE-02 go/no-go spike with the documented extraction fallback — on both
the server delivery side and the embedded SDK cores (wasm for
TypeScript, C ABI for .NET). Use is server-authoritative: the server
remains the single writer of record; clients receive authoritative deltas
and submit mutations for server validation. Member-to-member replication
keeps the accepted W4 data plane (ADR-0004 D-1..D-4) unchanged, with
openraft as the escalation-gate fallback. Full CRDT multi-master
replication is rejected: it would reopen ADR-0004's leader, quorum, and
fencing decisions and break V1 parity obligations (conflict
notifications, change vectors, `ConflictRecord`).

### D-4 — NATS where its semantics fit

Member transport lanes may ride NATS per PD-2026-08-26-01 D3, aligned
with the NATS-native Orchestrator control-plane spec: JetStream streams
with durable consumers for acknowledged, replayable delivery; core
pub/sub for lossy observation; and strict account, trust-root,
credential, and subject-namespace separation from the control plane.
Per-link-class placement is decided with evidence in the transport ticket
(SE-06), not mandated here.

### D-5 — dependency philosophy amendment

MIT/Apache-2.0 dependencies are permitted for the sync core (Loro),
member transport (NATS clients), TBEP (`quinn`, `rmp-serde`,
`MessagePack-CSharp`), and the C# families (workspace-pinned .NET SDK;
`System.Text.Json` only for JSON surfaces). The engine's storage, WAL,
engine, replication, and streaming modules remain zero-dependency;
`#![deny(unsafe_code)]` and the deterministic test discipline are
unchanged. Claim discipline (ADR-0003) is unchanged: nothing in this
record is a performance or readiness claim.

Amended 2026-08-27 by the parent (charter PD-2026-08-27-01, ratifying
the GAP-S2 decision record
`docs/storage/encryption-key-management.md`): the storage module gains
one carve-out — vetted cryptographic dependencies (the RustCrypto AEAD
family; `chacha20poly1305` 0.11.0, XChaCha20-Poly1305 — V1's exact
algorithm family) are permitted for encryption at rest. Rationale:
PB-5011 sits on a `critical` risk row, and the zero-dependency
alternative would extend BK-04's in-crate composition whose recorded
non-claim ("nothing about vetted-cipher strength") is untenable as the
integrity basis for the entire database at rest. The carve-out is
narrow: storage-at-rest cryptography only; no other engine module gains
dependency permission from this amendment. The `r-efi` LGPL-option
exception (already in the lock via `getrandom`, permissive at choice)
is ratified as a recorded license disposition.
