# Strict auth persistence

Date: 2026-09-05

Status: Partially implemented. Production composition, the complete Better Auth
adapter, immutable contract publication, and public failure qualification remain open.

Authority: The workspace goal requires identity correctness during partitions,
permits workers to choose coordination, and keeps ordinary document consistency
unchanged. ADR 0044 supplies causal replication, not strict identity authority.

## Source review

`terrabase-auth` now supplies the strict consensus state machine and encrypted
storage. `terrabase-server/src/auth_runtime.rs` exposes named auth operations,
including a durable transaction lifecycle. The TypeScript HTTP client covers
those operations, but the complete Better Auth operations port still lacks its
production implementation. The dashboard development path still
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

The producer uses optimistic transaction workspaces with a captured global
revision, a 30-second lifetime, at most 100 operations, and at most 4 MiB of
staged state. At most 32 workspaces may be active. Begin accepts a client label
of at most 100 UTF-8 bytes and returns a generation-qualified identity. Every
subsequent operation, commit, and rollback uses that exact returned identity.
The generation counter persists through restart and snapshots; terminal
workspaces can therefore be removed without allowing a delayed command to
target a later callback. Retrying Begin uses its original idempotency key.

Commit requires the captured revision to remain current. Hard state-machine
failures, including operation-limit and conflicting-idempotency rejection,
poison the workspace and discard staged commands. An identical successful
retry returns its retained outcome. Conditional nonmatches remain available
for deliberate handling by the callback. Transactional record reads carry
both the exact transaction identity and a stable idempotency key, and read
the replicated workspace rather than committed state.

HTTP validation failures that never reach consensus do not establish an
aborted workspace. The complete SDK callback adapter must stop commit after
such failures and resolve uncertain outcomes before retrying. It must never
rerun a callback to recover an uncertain commit. These public failure cases
and Better Auth's maintained transaction tests remain qualification work.

The state machine now provides predicate-based update-one, update-many,
delete-one, delete-many, consume-matching, increment-one, and exact count.
Single-record operations choose the lowest matching ID. Bulk mutations affect
at most 1,000 records and at most 1 MiB of aggregate record data; exceeding a
bound rejects the entire operation. Replacement records and their unique keys
are checked before any write. Signed floating zero has one unique-index key,
including when nested in JSON values.

Increment applies all requested numeric fields and optional set fields
atomically. It rejects ID changes, overlapping increment/set fields, integer
overflow, and nonfinite results. Integer arithmetic preserves the full signed
and unsigned 64-bit ranges. Mixing floating-point increments with integer
operands outside the exact binary64 integer range is rejected instead of
silently discarding counter precision. Integer predicate ordering also retains
64-bit precision.

Predicates follow the Better Auth 1.7.2 Kysely/Drizzle grouping contract:
all AND terms must match, and at least one OR term must match when OR terms
are present. Request order does not change that grouping. An OR term cannot
bypass an AND tenant guard. The pinned memory adapter uses different grouping
and does not qualify this SQL-backed predicate contract.

Missing fields count as null. Equality and inequality against null act as null
checks; inequality against a non-null value excludes null records. Nonempty
`not_in` excludes null records and matches nothing when its list contains null.
Empty `in` matches nothing and empty `not_in` matches everything. Insensitive
membership folds strings only when every list element is a string.

Ordinary count and page queries require read permission and a quorum-confirmed
applied barrier. They do not create idempotency records or advance the mutation
revision. Transactional queries require the returned transaction identity and
a stable idempotency key and execute against the replicated workspace. The
public auth routes contribute their schemas to the same OpenAPI document used
by the served JSON, Scalar, and contract exporter.

Admitted followers route client operations to the elected voter over mutually
authenticated member QUIC. Receivers execute forwarded operations locally to
prevent forwarding loops; no leader or failed transport returns a bounded
unavailable outcome. Forwarded requests have a separate eight-task limit so
waiting for replication does not consume the consensus dispatch pool. Peer
handshake and connection failures do not terminate the listener. Finished
transport frames wait for peer acknowledgement before connection disposal;
this acknowledgement does not replace application or durable replication checks.

Consensus currently uses a two-second heartbeat/RPC interval and six-to-ten
second election timeouts to accommodate fresh authenticated connections. These
initial settings require WAN latency and failover qualification.

Auth pages support field projection, scalar sorting, and explicit one-level
relations. Relation requests name both models' fields and the output field;
model-name conventions do not infer relationships. Joins resolve against the
same applied or staged SQLite snapshot before base-field projection. One-to-one
joins return an object or null; one-to-many joins return an ID-ordered array.
Joined fields survive base projection, and output-field collisions are rejected.

Pages allow at most 64 selected fields and eight joins, with page offset, page
limit and per-join limit each at most 1,000. Sorting uses null, boolean, numeric,
string, array and object type order, preserves exact integer comparisons, and
breaks ties by ascending ID even for descending sorts. The retained sort/page
window is limited to 4 MiB, the result array to 1 MiB, and total examined base
and related bodies to 64 MiB or one million rows. Bound violations return an
invalid operation without partial output. Unsorted pages stop once filled.
These limits bound current implementation work; indexes and intended-hardware
latency qualification remain required for launch performance claims.

### Reserved member transport capacity

Member protocol version 2 carries a connection class in its authenticated hello
and uses ALPN `terrabase-member/2`. Each admitted peer may hold one replication
session plus one inbound and one outbound session for each of forwarded auth
clients and auth consensus: five sessions total. Direction comes from the local
connect or accept path. Outbound attempts reserve their slots before dialing;
the receiver reserves its slot before acknowledging the handshake. Pending
reservations survive status sampling and are released on failure or cancellation.
Auth RPC dispatch
checks that the method matches its declared class before invoking the authority.
Physical/causal replication accepts its initial frame only on the replication
class. Version 1 peers must upgrade together; no compatibility path is retained.

The configured total operation ceiling remains unchanged. Four independent
quotas divide it between handshakes and the three connection classes; each gets
the integer quotient, and handshakes receive the remainder. The handshake quota
is split between inbound and outbound work so outgoing handshakes cannot occupy
the capacity needed to accept peers. Configuration
requires at least two operations per admitted peer in each application class
so persistent receives leave capacity for sends. Idle listener waits consume
no operation permit. Cancellation and drain release both class and total
accounting. Forwarded clients cannot consume the frame-operation permits needed
to replicate the writes they are awaiting.

Shared serving requires one accept owner that dispatches these classes. The
standalone auth listener and replication listener must not race for the same
endpoint. Completing that production composition and qualifying multiple
groups, peer failures, and WAN load remain required.

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

This design is not qualification evidence and does not pass an auth release gate.
