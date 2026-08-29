---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Coordinated consensus and transaction recovery

## Scope

Ordinary TerraBase mutations remain leaderless native CRDT commits. This
record defines the separate coordinated lane used only for invariants that
cannot merge safely:

- linearizable compare-and-swap;
- uniqueness and one-time consumption;
- authentication session rotation and revocation;
- single-Partition serializable transactions;
- replica-set and membership decisions; and
- other explicitly registered coordinated operations.

The lane is unavailable without its quorum. It never weakens an operation into
an eventually consistent write and never becomes the path for ordinary reads or
mutations.

## Paxos log per Partition

Each logical Partition owns one durable Multi-Paxos decision log. Every active
replica seat in every selected Region is a voter. With `R` selected Regions,
the normal voter count is `2R` and a decision requires
`floor(voters / 2) + 1` current voters. Backup Members never vote.

Each log record contains:

```text
ConsensusRecord {
    database_ulid
    partition_ulid
    slot
    ballot_counter
    proposer_replica_ulid
    replica_set_epoch
    client_request_ulid
    command_kind
    command_digest
    expected_state_version?
    accepted_value
    quorum_certificate
}
```

`slot`, `ballot_counter`, state versions, and epochs are unsigned numeric
coordinates, not entity IDs. A ballot orders `(ballot_counter,
proposer_replica_ulid)` lexicographically. A voter persists its promised ballot
and accepted value before acknowledging them. A decided record is immutable.
The quorum certificate names the exact voter set, ballots, value digest, and
replica-set epoch that proved the decision.

Any admitted voter may propose. A successful proposer may retain a short-lived
optimization lease and use Multi-Paxos phase two for later slots, but the
persisted ballot and quorum intersection define safety. Wall-clock lease expiry
never authorizes a conflicting decision. A stale proposer is fenced by the
higher ballot and cannot commit ordinary data or membership.

## Ordered application and reads

Voters apply decided records in increasing slot order. Missing slots are
recovered before a later slot becomes visible. Application persists the
decision, resulting coordinated state version, idempotency outcome, and any
native commit it authorizes in one recoverable state machine.

A linearizable read uses a current quorum read barrier or a valid proposer
lease whose ballot is still accepted by a quorum. A local read without that
proof cannot claim coordinated consistency.

The lane deduplicates by client-request ULID and canonical command digest.
Retrying the same command discovers the existing accepted or decided value.
Reusing the ULID for a different digest returns `IdempotencyConflict`.

## Single-Partition transactions

A transaction reads from one named MVCC snapshot and records its bounded read
set, predicate/index dependencies, write set, schema generation, authorization
generation, and deadline. Commit proposes one deterministic certification
command to the Partition log.

The decision function validates:

- every read version and predicate dependency against the prior decided state;
- uniqueness, one-time, relationship, and schema constraints;
- current authorization and replica-set epoch;
- write count, byte, CPU, memory, and deadline bounds; and
- the client-request ULID and command digest.

It decides `Committed` with one canonical native commit or `Aborted` with a
stable typed reason. Recovery cannot change an abort into a commit or mint a
second commit. General cross-Partition transactions are not part of this
protocol.

## Membership changes

Membership uses joint consensus. The old configuration first decides the joint
old-and-new voter set. Entries in the joint epoch require a majority of the old
set and a majority of the new set. After every new voter reaches the required
verified frontier, one joint decision activates the new set.

A removed voter cannot promise, accept, decide, or originate under the new
epoch. If the old quorum is irrecoverably lost, normal consensus cannot repair
it. The separately authorized disaster-recovery procedure creates a new
cluster generation, records the surviving frontier and accepted data-loss
boundary, and never fabricates an old quorum certificate.

## Bounds and availability

Commands, read sets, write sets, certificates, outstanding slots, proposer
queues, recovery scans, and undecided state have explicit byte and count
limits. One tenant cannot monopolize a voter or proposer. Admission reserves
the resources needed to finish or safely abort an accepted command.

At one selected Region both local Members are required. At two selected Regions
three of four voters are required. At three selected Regions four of six voters
are required, so coordinated work survives loss of one complete Region. A
missing quorum returns `CoordinationUnavailable` with no partial invariant.

## Evidence

Qualification includes:

- a TLA+ or equivalent checked state-machine model for ballots, slots, retry,
  joint consensus, crash recovery, and stale proposers;
- randomized histories checked for linearizability and serializability;
- coordinator and voter failure before and after every durable boundary;
- competing proposers in every Region under delay, loss, duplication, and
  reordering;
- old/new quorum overlap, erased voter state, and disaster recovery;
- duplicate and conflicting client-request ULIDs;
- bounded queue and noisy-tenant saturation; and
- WAN latency and throughput receipts under ADR 0026.

No test may substitute an in-memory lock, one-process transaction manager, or
single-leader database for the persisted protocol.
