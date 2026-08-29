# P0 contract: TBEP compatibility rules

> Superseded version 1 contract. Current TBEP is
> `terrabase.engine.protocol/2`, generated MessagePack over QUIC with mTLS,
> including loopback. See `docs/sync/tbep.md`. UDS and repository-local C#
> hosts below are migration history.

Status: frozen by the P0 contract pass, 2026-08-28. Governs P4
implementation and the cross-language conformance suites. Parents: ADR
0013 (server/client/bindings/efficiency), ADR 0018 D-1 (no leadership
vocabulary). Substrate: `crates/terrabase-tbep` (versioned typed
MessagePack frames, generated Rust/C# contracts, UDS server) and
`host/TerraBase.Protocol` (multiplexed C# channel).

## 1. Versioning and negotiation

- One generated schema is the single source of truth for Rust and C#
  (`crates/terrabase-tbep/schema`). Wire identity: `terrabase.tbep`,
  schema `major.minor`.
- Connection open = capability/version negotiation: each side sends its
  supported version set and capabilities (compression by frame class,
  batching, streaming lanes, admin surface). The peers select the
  highest common version; unknown capabilities are ignored; a
  version-mismatch fails closed with a typed `ProtocolVersionMismatch`
  before any operation frame.
- Compatibility rule: a minor bump may ADD operations, fields, and
  error codes; it may never change an existing operation's request or
  response shape, remove an operation, or narrow an error. A major bump
  is a new protocol and requires explicit negotiation support on both
  sides.
- The generated contracts carry a parity-mapping column (SE-14 rider):
  every operation names the parity rows it serves.

## 2. Frame rules

- Bounded, versioned, typed MessagePack frames (existing shape). Every
  frame carries: version, stream id, operation, flags, payload-length
  bound. Frames exceeding the bound fail closed.
- Multiplexed requests over independent streams; independent streaming
  lanes with per-lane flow control; cancellation is a first-class frame
  (idempotent); idempotent retries carry the same operation identity;
  explicit overload responses (typed `Overloaded` with retry guidance)
  rather than silent drops or unbounded queues.
- Compression is negotiated by frame class and enabled only when
  benchmark receipts show a net win (none enabled by default today).
- Bindings: UDS (same-host; the currently qualified binding) and
  QUIC+TLS (the supported network binding; not yet qualified — recorded
  gap). Both carry the identical frame layer. QUIC 0-RTT is forbidden
  for mutations. Client tokens: short-lived tenant-scoped over TLS 1.3;
  member links: mTLS with rotated identities.

## 3. Forbidden vocabulary (hard compatibility rule)

TBEP MUST NOT grow, and existing surfaces must shed: `NotLeader`,
leader/master/primary discovery or endpoints, redirect-to-primary,
election terms, failover promotion, or backup-to-active promotion
operations. A client retries the same operation through any other
active Member; the protocol offers no leadership notions to express.

## 4. Required typed surface additions (P4)

- Cluster mode, replication position, and read lag on reads where
  applicable; cold-read source in tier-affected read receipts;
  `BackupAck` receipts; typed `BackupClusterReadOnly`,
  `BackupClusterImmutable`, `ColdTierUnavailable`,
  `FrontierUnreachable` errors (P0 error names are frozen here).
- Typed administration for the Storage Tier Mover: configure, explain,
  dry-run, pause, resume, cancel, inspect, receipts — never raw S3
  credentials or a generic object-store escape hatch.

## 5. Conformance

Cross-language conformance vectors (`crates/terrabase-tbep/vectors`)
run in CI for every generated binding. A vector failure is a release
blocker. The C# protocol library, host, CLI, and Studio build/test in
the same CI as Rust (AC-8): one gate owns the cross-language product.

## 6. Honest limits

The QUIC client binding, TLS identity, remote admission, capability
negotiation, and the §4 additions are `unresolved` until P4 lands.
Today's qualified surface is the UDS binding with the existing
operation set; the current tree also carries no leadership vocabulary
to remove (the rule binds future additions and P2's replication
surface).
