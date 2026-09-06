# Cleanup and webhook verification, 2026-09-05

Source baseline: `56d45c2`, with the working-tree changes described below.

## Cleanup review

The five tracked root files `deadprobe`, `deadprobe2`, `deadprobe3`,
`deadprobe4`, and `deadprobe5` were Linux ELF executables containing debug
information. Searches of source, scripts, manifests, documentation, and build
inputs found no references to those names. Cargo's explicit binary targets are
source files; neither the Makefile nor the stack scripts consume these probes.
They were deleted as generated leftovers, removing about 21 MB of tracked
binaries. The ignored `rust_out` and `target/` local outputs were left alone.

The external backup adapter remains active: server `backup.rs` launches its
configured Litestream executable for streaming and restoration. It cannot be
removed until the native replacement meets ADR 0046's restore-chain,
encryption, residency, multi-database resource, and upstream comparison gates.
This is an unfinished retirement dependency, not a passed G1 gate.

## Webhook destination security

The HTTPS adapter already resolves each delivery attempt and pins the resulting
addresses into its transport. Its IPv6 policy previously admitted translation,
tunnel, and deprecated site-local destinations, including NAT64 addresses that
encode private IPv4 hosts. Admission now requires native global-unicast IPv6
outside the special-purpose and documentation blocks; mapped IPv4 still passes
through the IPv4 policy. This applies both when registering an endpoint and on
every delivery attempt.

Regression coverage includes mixed public/nonpublic DNS responses, IPv6
translation and tunnel addresses, native public IPv6, and a public initial
503 response followed by a NAT64-private DNS answer on retry. The retry is
rejected without a second network send. These are adapter-level tests; real
provider routing, redirect behavior, and the broader release gates still need
their own evidence.

The native transport now uses the existing Rustls/Hyper dependencies with
Mozilla roots, original-hostname verification, and direct TCP connections to
validated addresses. It neither reads proxy settings nor follows redirects.
DNS jobs retain one of sixteen permits until completion even if their caller
times out. HTTPS attempts share that admission budget and have a ten-second
total deadline; response processing ends after bounded headers, without
consuming arbitrary response bodies. Cancellation drops the connection future
and socket. `WebhookPorts::native_https` provides concrete transport composition
alongside the caller's durable journal, storage, secrets, and guard authorities.
Attaching the service to the member's main router remains separate work.

Validation:

- `nice -n 19 cargo test -p terrabase-server --locked webhook_api --lib`: 15
  tests passed, including real TLS on local sockets, hostname mismatch,
  redirect refusal, admission and deadline bounds, plus durable retry tests.
- `nice -n 19 cargo clippy -p terrabase-server --all-targets --locked -- -D warnings`:
  passed after native transport changes.
- The initial `nice -n 19 make check` passed workspace Rust tests and doc tests,
  then failed contract freshness because a concurrent TBEP placement proposal
  change was not yet regenerated. It is not a green whole-repository check;
  rerun after the coordinated contract and runtime changes settle.

The native transport tests validate real TLS and HTTP behavior on local
sockets, not intended-provider egress or full production composition.

## Explicit extracted-index removal

`Engine::remove_index_field` now deletes the requested durable descriptor
atomically. Other collections retain shared fields and indexes. Removing the
last reference drops both extracted columns and their physical indexes, updates
the write-statement cache, and releases the extracted-column budget. A hidden
index still depending on the field produces a typed conflict before mutation.
Document rows, their publication watermark, and customer usage remain unchanged.
A later query can register the field again through normal auto-indexing.

Three regression tests passed in the freshly compiled engine test artifact
`target/debug/deps/terrabase-05cd94d5adf30ccf`: shared-reference removal and
restart with preserved documents, atomic hidden-dependency refusal, and recovery
of an exhausted 32-field budget. Whole-workspace verification remains pending
concurrent runtime and dependency changes.

## Uniform durability admission

The transaction can now retain the exact causal frontier proven uniform by
its caller. SQLite compares that frontier inside the writer transaction after
idempotent receipt lookup and before mutating documents, usage, or metadata.
A newer dependency rejects admission without binding a permanent rejection;
the caller can prove a fresh frontier and retry the same logical operation.
Committed retries still recover the original receipt before checking the stale
frontier. This closes the gap between a public GlobalDurable preflight and
actual commit. RF=2 and cross-Region receipt waits remain separate mandatory
runtime checks; the guard alone does not establish global durability.

New regression coverage stages a guarded write, publishes an independent new
dependency, verifies refusal without a document, retries after a fresh cut, and
replays the original receipt. This test and the additional index DDL rollback
test await the coordinated current-source test build.

Native HTTPS admission was also moved to one process-wide semaphore. Creating
one transport per database or request cannot multiply its sixteen-slot limit.

## Auth transaction review and verification

Astra review found transaction identity reuse, incomplete poisoning after
rejected inner operations, and transactional record GETs reading committed
state. Begin now returns an exact generation-qualified identity from a durable
counter retained in snapshots. Terminal workspaces can be removed safely.
Hard state-machine failures clear staged writes and refuse commit; identical
successful requests still replay their retained result. Transactional GET now
uses the replicated workspace, and the TypeScript client retains its explicit
GET idempotency key.

All 13 `terrabase-auth` library tests passed on the working tree based on
`946c52e6b4ba1b9d2e557bc1c09f310aeddf509c`. Coverage includes delayed commands,
expiry/restart, snapshot restore, simultaneous owner-departure transactions,
operation limits, and caught invalid/idempotency failures without visible staged
writes. The verified implementation SHA-256 values are
`8837347fce121c4b14c3ba925e38e6293793d7815012efb71cb61fbf37b24ac3`
for `transactions.rs`,
`091e5add3e5ed547c539a04930a7be7dc13d20771e10f26a69af910045d9264f`
for `store.rs`, and
`9270b8c299782de9d78fabb84235d109ffa727de8e8bf52871db7240a11ac36b`
for `tests.rs`, all under `crates/terrabase-auth/src`.

The TypeScript auth transport tests also passed, including the 100-byte Begin
label boundary and returned identity. These are library and transport-adapter
results. Actual admitted QUIC/encrypted HTTP journeys, the complete Better Auth
callback adapter, and production startup remain unfinished. Pre-consensus HTTP
validation errors still require the callback adapter to prevent commit and
resolve or roll back its transaction; they do not prove durable poisoning.

The broader server test build subsequently failed at the missing
`ReplicationRepairCut::baseline_digest` producer method. That integration is
being repaired while preserving the ULID and replication changes. No server
suite or whole-workspace pass follows from the auth result.
