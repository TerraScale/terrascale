# 0009: One catalog primary, log-shipping followers

- Status: proposed (TS-561, 2026-10-06); needs human acceptance
- Scope: `Engine` (system database capture, `ISystemReplication`), `Features/Replication` (catalog stream),
  `Features/Management` (followers write nothing), `Features/Health` (readiness), `docs/replication.md`

## Context

TS-464 replicates client-database documents only. The management catalog, API keys, dashboard documents and
commit records live in `_system.db` ([0005](0005-multi-tenancy.md), [0008](0008-management-commits.md)) and stay
local, so a second member answers 401/404 for organizations and keys created on the first. Only one member can
serve, and every restart is downtime.

The catalog cannot use the leaderless merge of client documents. Its writes check invariants inside their
transaction (unique names per parent, the last owner, fences that compare-and-set a version, single-use
invitation tokens). Two members that each accept such a write concurrently can both pass the check, and a
per-document merge cannot undo one of them. Its transactions are also larger and differently shaped than client
change groups: an organization deletion removes every audit event one by one, a commit may write thousands of
documents, collection names hold `/`, and documents may be JSON scalars.

## Decision

**One writer.** Exactly one member is the catalog primary; every other member is a follower with
`catalog-primary=https://<primary>:<peer-port>`. The primary is the only member that writes `_system.db`, so
every check that runs in its write transaction stays atomic for the whole cluster. A member without
`catalog-primary` is a primary, as every member was before. Choosing the primary is configuration: there is no
election. Failover is a configuration change ([Failover](#failover)).

**Followers write nothing.** A follower's engine refuses every write transaction on `_system.db` except its
replication applier's. A Management write, an API key issue or a data-plane database creation that reaches a
follower answers `503 not_primary` with `Retry-After: 1`; nothing is written. Its hosted catalog writers do not
run: startup intent recovery and the file audit, catalog migration and backfills (the primary's are replicated),
the backup scheduler, and the usage and query-field flushes (their counts stay in memory, bounded by the
databases served, and are lost on restart: they are best-effort dashboard statistics, not billing). Reads
(authenticating API keys, resolving tenants, Management reads) use the local replica.

**Capture.** Every committed write transaction on `_system.db` of a primary appends one group to the
database's existing journal (`changes`, chained `journal_prefixes`), in the same SQLite transaction, under the
database's origin. Unlike client groups, a system group keeps every mutation in order (no final-state collapse),
needs no client key or object rules, includes the per-document deletions of `DeleteCollectionsAsync`, and is
bounded by 262,144 changes and 64 MiB instead of 1,024 and 2 MiB. A larger transaction fails before commit,
as an oversized client transaction does. The journal keeps its last 1,024 groups (about three hours of 10-second
usage flushes); each append drops the group that falls out, and a follower further behind gets a snapshot.

**Lineage.** A follower's snapshot adopts the primary's origin as its own `_system.db` incarnation. Its journal
head is then the primary group it applied last: one digest row, no group bodies. So the follower's position, its
dashboard document ETags and, after a promotion, its next group continue the primary's journal.

**Stream.** A follower holds one `CatalogReplication.Follow` stream to the primary over the peer listener's
mutual TLS. It sends its journal head: origin, sequence and prefix digest.

1. The primary answers `Welcome` with its origin and journal head.
2. When the head names its origin, is past sequence 0, its digest at that sequence matches and the primary still
   holds the next group, the primary sends every later group. (Sequence 0 proves nothing about the documents written
   before capture began, which a file restored under the same origin may differ in.) Otherwise (a new follower, a promoted primary that lacks the groups, a
   restored file, a follower behind the retained tail) it sends a snapshot first: `SnapshotBegin` with its head
   `S0` (read before the first page) and that digest, every live document in key order in bounded pages, each
   read in its own short read transaction (no transaction spans a network write), then the groups `S0+1 … S1`
   committed meanwhile (`S1` read after the last page), then `SnapshotEnd` with `S1`, its digest and the
   primary's commit offset.
3. Groups and snapshot pages travel in the TS-464 `TBLZ4E01` LZ4 envelope, at most 4,096 changes and 2 MiB of
   content per page (one larger document travels alone). A group is `GroupBegin` (sequence, change count),
   its pages, and `GroupEnd` (digest).
4. After the head, the primary waits for its next commit and sends a `Heartbeat` with its head at least every
   5 seconds.

The follower applies a group in one SQLite transaction spanning its pages: each change replaces the
document's exact state (version and the primary's bytes, or no document), then it checks the sequence is its
head plus one and recomputes the chained digest, and stores the new head in the same transaction. A snapshot is
one transaction too: it removes every document and the follower's own journal, adopts the origin, applies the
pages and the replayed groups, checks `S1`'s digest, stores the head and raises the commit offset to the
primary's, so versions written after a promotion stay above every replicated one. A reader therefore sees the
catalog of one primary commit, never part of one. A gap, a digest mismatch, an invalid page or a stalled stream (15 seconds
without a message) rolls back, ends the stream and reconnects with backoff (100 ms doubling to 10 s, full
jitter). Replaced documents move the write stamps, so cached API keys and catalog reads are invalidated.

**Readiness.** A follower's `/readyz` answers 503 until it has applied everything up to the head of one
`Welcome` or `Heartbeat` of its current process, and again whenever its primary reports (after a reconnect) a head
it has not applied yet. A primary that cannot be reached leaves the last head it reported, so a caught-up follower
stays ready while the primary restarts and is not taken out of a load balancer; `terrabase_catalog_follower_lag`
and `terrabase_catalog_follower_connected` show how stale it may be. A primary is ready as before.

Readiness does not wait for client-document replication. A member's `terrabase_replication_sender_durable_lag`
grows while any peer restarts, so gating on it would take every member of a two-member cluster out of rotation
during a rolling restart; and `terrabase_replication_sender_initialized` stays 0 for as long as one client database
file cannot be opened, which would keep a member out for good. A drain instead waits for the durable lag of the
member it stops to reach 0 while its peers are up.

**Followers serve registered databases.** A database the replicated catalog names is served by a follower even
before any of its documents arrived: the data plane creates the missing local file, which then receives
replication like any other.

**A catalog is never discarded for another origin.** A follower takes a snapshot only when its catalog is empty
or belongs to the primary's origin. A populated catalog of another origin (the member's own, a primary restarted
on a new volume, a mistyped address, another cluster's primary under the same CA) is kept: the follower counts the
refusal (`terrabase_catalog_follower_failures_total{reason="lineage"}`), logs an error and keeps serving what it
has; a member that has not caught up in this process stays not ready. To follow a different catalog on purpose,
remove the member's `_system.db` files.

## Failover

The primary is a single point for catalog writes, not for serving: followers keep authenticating keys and
serving documents while it is down. To replace a lost primary, pick the follower with the highest
`terrabase_catalog_applied_sequence`, remove `catalog-primary` from it and restart it, then point the other
followers at it. It continues the same origin, so they resume or take a snapshot from it. Groups the old
primary committed but no follower applied are lost, as with any asynchronous replica; a follower that had
applied more than the new primary gets a snapshot that replaces its catalog. A demoted primary pointed at the new
one does the same. Nothing detects two members running as primary of one origin yet: never start a former
primary without `catalog-primary` while another one serves.

## Consequences

- Management writes must reach the primary until a follower can forward them (TS-561 follow-up); a load
  balancer in front of all members either routes `/v1/management` to the primary or retries `503 not_primary`.
- Followers lag the primary by one stream round trip. A key issued on the primary authenticates on a follower
  once that follower applied it; a revoked key stops authenticating there at the same point.
- `Follow` serves the whole catalog (member emails, key and invitation hashes, dashboard documents) to any
  member whose certificate chains to `peer-client-ca`: use one peer CA per cluster.
- A catalog version change takes the data plane of followers down until primary and followers read the same
  version (a follower answers 503 for a catalog version it does not support, and migrates nothing). A build that
  changes the catalog version must read the previous one too; none has yet.
- Every system transaction, not only Management commits, is bounded by 262,144 changes and 64 MiB. Known writers
  that can reach it: deleting an organization with more than about 130,000 retained audit events, a usage flush of
  more than about 130,000 databases active within 10 seconds, and the one-time migration of a version 1 to 4 catalog
  of that size. Each needs paging into bounded transactions before a catalog that large exists (follow-up).
- The retained tail counts groups, not bytes: 1,024 groups of usage flushes of many databases can hold gigabytes.
- A follower that starts while its primary is unreachable stays not ready until the primary is back: roll one
  member at a time.
- Followers do not flush usage or query-field counts, so dashboards count only the primary's traffic (follow-up:
  forward them).
- A follower serves a registered database from an empty local file until client replication delivers its
  documents; for a branch or restore of a populated database that is a short window of missing documents.
- Index definitions live in each member's copy of a client database and are not replicated yet (TS-561
  follow-up). Database deletion is still not replicated to client-database files, and a client-replication
  receiver still creates databases the catalog does not register.
- Tombstones of dashboard documents are not replicated: a follower holds no row for a deleted document.

## Alternatives considered

- **Leaderless catalog with the client merge policy:** two members can both pass a uniqueness, last-owner or
  fence check; the merge cannot restore the invariant.
- **Push the system database through the client `Sync` stream:** its groups exceed the client group and message
  limits, its keys and scalar documents fail the client admission contract, an older receiver would treat it as
  a client database, and a follower could not tell which peer is the primary.
- **Copy `_system.db` files (SQLite backup API):** O(catalog) per change, and it would overwrite per-member state.
- **Consensus (Raft) for automatic failover:** far larger; the configured primary is enough for zero-downtime
  rolling restarts.
