# Strict auth persistence

Date: 2026-09-05

Status: Proposed implementation design. No public auth operation or production
consensus implementation is supplied by this record.

Authority: The workspace goal requires identity correctness during partitions,
permits workers to choose coordination, and keeps ordinary document consistency
unchanged. ADR 0044 supplies causal replication, not strict identity authority.

## Source review

The server's public API has no Better Auth persistence operations.
`ts-typescript/src/better-auth.ts` defines a structural operations port without
a generated public producer binding. The dashboard development path still
consumes the temporary SQLite bridge. Its deletion depends on qualified public
persistence, so it remains. Both local bridge implementations now refuse
transaction callbacks before executing effects because neither supports atomic
callback transactions.

`terrabase-cluster` supplies placement admission and physical RF=2 WAL delivery.
Member health opinions, placement proposals, and physical durable receipts do
not establish exclusive authority for a conditional auth operation. The former
cluster quorum implementation was retired and is not a usable dependency.

## Proposed mechanism

Use an established Raft implementation, evaluated against its maintained storage
and network conformance tests, for a strict auth namespace within each database.
Store its log, votes, snapshots, applied records, and idempotency outcomes in
SQLite on OCI. Do not place user identity data in NATS or Workers. Use the
existing authenticated member transport boundaries with a separate versioned
data-replication protocol; TBEP remains control traffic.

The group admits only hosts in the database's immutable placement. Its durable
bootstrap record fixes group identity and the initial voter configuration.
Restart cannot bootstrap a new group from missing state. Subsequent membership
changes require joint consensus and verified snapshot/log catch-up. A member
health opinion or a pending placement proposal never grants leadership.

Choose the voter mapping against the existing two-host-per-Region topology
before implementation. Each voter needs independently durable consensus state;
a physical WAL follower cannot impersonate an independent voter. Majority
commit and the existing regional RF=2 durability promise must both hold before
acknowledging a successful mutation. Neither an RF=2 receipt alone nor a
Raft commit alone proves both requirements. Loss of the necessary members
refuses security-sensitive work rather than reducing either threshold.

Keep auth state outside causal merge and ordinary document writes. Expose the
namespace only through named, scoped public operations so generic put/delete,
imports, branches, restore, and migrations cannot bypass its invariants.
Reopening restored auth state requires safe group bootstrap and revocation
handling; copying an old snapshot must not resurrect consumed credentials.

Apply unique insert, conditional update, one-time consume, bounded batch
transactions, index changes, and their stable idempotency outcomes in the same
SQLite state-machine transaction. Deduplication binds tenant, database,
operation identity, and canonical request digest. Reusing a key with different
input fails deterministically. A lost response is resolved by replaying the
same identity, including the original one-time consume result.

Security-sensitive reads use a quorum-confirmed read barrier and wait for the
corresponding applied index. Reject operations when the barrier, required
durability, or group identity cannot be verified. Local health, a cached leader
hint, a session token, or a clock lease alone does not authorize a read or write.
Ordinary causal document operations retain their existing guarantees.

The public transaction contract must represent bounded commands and
preconditions. An arbitrary JavaScript callback that reads intermediate results
cannot simply become a batch. Either the producer implements a bounded,
durable transaction lifecycle with conflict/expiry handling, or the SDK refuses
that callback shape before effects. Publish this choice before adapting Better
Auth's maintained transaction tests.

## Implementation and qualification dependencies

Integrate the consensus storage and transport in the real member startup path,
including credential validation and refusal of incomplete bootstrap state.
Publish immutable OpenAPI and route manifest artifacts only with implemented
handlers. Generate the TypeScript binding from that producer and use it from
the dashboard through the same public authorization as customers.

Qualify competing unique inserts, conditional updates, consume retries after a
lost response, stale leader reads, voter partitions, process crashes between
log persistence and application, snapshot installation, membership changes,
revocation, and restore. Verify response loss cannot duplicate an operation and
minority members never admit strict work. Run those cases through the real
public API, then the exact Better Auth core/plugin adapter suite. Retire the
temporary bridge only after the replacement and dashboard flows pass.

This design is not implementation evidence and does not pass an auth release
gate. Voter mapping and transaction lifecycle remain concrete design work.
