# 0009: One catalog primary, log-shipping followers

- Status: superseded by docs/terrabase/multi-region-replication.md section 15 (R10a): the catalog primary, followers and forwarding are deleted
- Scope: `Engine` (system database capture, `ISystemReplication`), `Features/Replication` (catalog stream),
  `Features/Management` (followers write nothing), `Features/Health` (readiness), `docs/terrabase/replication.md`

## Context

TS-464 replicates client-database documents only. The management catalog and API keys live in `_system.db` ([0005](0005-multi-tenancy.md)) and stay
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
replication applier's: a catalog write that reaches it answers `503 not_primary` with `Retry-After: 1`, and nothing
is written. The requests that write the catalog are forwarded instead (Forwarding, below). Its hosted
catalog writers do not run: startup intent recovery and the file audit, catalog migration and backfills (the
primary's are replicated), the backup scheduler, and the usage and query-field flushes. Every 10 seconds it sends its
usage counts to the primary over `CatalogUsage.Add` instead (TS-581), which adds them to its own; its query-field counts
stay in memory, bounded by the databases served, and are lost on restart (best-effort dashboard statistics, not
billing). Data-plane reads (authenticating API keys, resolving tenants) use the local replica.

**Forwarding.** A follower forwards a client-listener request the primary serves to the primary over
`CatalogForwarding.Forward`, on the peer listener's mutual TLS, once routing, the listener and region-pin checks,
authentication and authorization passed locally: every Management API request (reads included, so they see every
catalog write), and a request to an endpoint marked `ServedByCatalogPrimary.WhenWriting` (data-plane database
creation, which never reads its body) that the follower refused because it would write the catalog; an existing
database is answered locally. It sends the method, decoded path, raw query, its headers without connection-specific
ones (those HTTP/1.1's `Connection` names too) and the region pin, its region, and its body (at most 4 MiB, one
`TBLZ4E01` envelope; larger answers 413). The primary serves it through its whole HTTP pipeline as if its own client
listener had accepted it, answering as the follower's region (`docs/terrabase/region-identity.md`): it authenticates,
authorizes and validates it again, and serves a forwarded request only on those endpoints (404 otherwise). It
buffers the response (at most 16 MiB, the first 64 KiB reserved from the memory budget before the request runs),
reads its catalog head, which every write of the request committed before, and sends the status, headers, body
length and that head, then the body in 1 MiB envelopes. The follower answers once it applied that head, so a key or
database created through it serves there at once; it waits at most 5 seconds, never past the forwarding deadline,
and not at all while it does not follow the primary's journal
(`terrabase_catalog_forwarded_total{result="lagging"}`). A primary that cannot serve the request (unreachable, not a
primary, at its 64 forwarded requests or out of memory) makes the follower answer `503 catalog_primary_unavailable`
with `Retry-After: 1`; one it refuses as malformed, 400. The follower holds each body chunk back until the next
frame or the call's successful end arrived, so a call that fails or sends more or less than its head declared is
answered 503 while nothing of it went out, and resets the client connection after. A forwarded request has 60
seconds, from reading its body to the end of the response: a body that does not arrive in time answers 408; a
primary that does not answer, or a client that stops reading the response, gets 503 (or a reset), and the primary's
request is cancelled. The administrative listener's requests are never forwarded: it stays loopback-only, and its
catalog writes (the operator's `POST /admin/api-keys`, the service credential's Management requests) answer `503
not_primary` on a follower.

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

**Deletions.** A database deletion commits its cleanup intent (`pending_database_cleanup`) in the same group that
unregisters it; the primary deletes its files and then clears the intent. A follower acts on every intent a group or
snapshot it applies, once that transaction committed, and before each stream on those its catalog still holds: when its
catalog, at this build's version, no longer registers the database, it deletes the local files. A deletion that fails
ends the stream (`terrabase_catalog_follower_failures_total{reason="apply"}`) and is retried before the next one, with
backoff: the follower applies nothing more, so the intent stays in its catalog, until it succeeds. An intent of a
creation in progress names a database no follower has files of yet. A follower never deletes a file only because its
catalog does not register it, as a catalog restored from an older backup would make it do; after it first caught up in
a process, it runs the primary's startup audit, off its stream, which counts and logs such files
(`terrabase_storage_unknown_files`) and keeps them. Every client-replication receiver takes a Sync stream only for a
database its catalog registers, and otherwise answers `STREAM_ERROR_UNAUTHORIZED`, retryable: a peer that still has a
deleted database cannot recreate it, and a follower takes a new database once its catalog applied the registration (the
sender retries). The receiver and the follower's data plane check the registration again after opening, so a deletion
applied in between removes the incarnation they created. A catalog at a version this member does not read registers
nothing it can tell: streams are refused and nothing is deleted on its account.

**A catalog is never discarded for another origin.** A follower takes a snapshot only when its catalog is empty
or belongs to the primary's origin. A populated catalog of another origin (the member's own, a primary restarted
on a new volume, a mistyped address, another cluster's primary under the same CA) is kept: the follower counts the
refusal (`terrabase_catalog_follower_failures_total{reason="lineage"}`), logs an error and keeps serving what it
has; a member that has not caught up in this process stays not ready. To follow a different catalog on purpose,
remove the member's `_system.db` files.

## Failover

Automatic authority selection is required by TS-773. The current primary/follower
implementation is not an automatic failover guarantee. Before any approved
recovery, stop old-primary writes, verify catalog lineage and the highest
applied sequence, and preserve a single writer. Readiness alone does not prove
all document replication is caught up. Manual configuration-switch recipes are
retired; this documentation authorizes no failover or destructive operation.

Unreplicated primary commits can be lost. A follower ahead of a replacement
primary may have its catalog replaced by that primary's snapshot. The current
runtime does not detect two primaries for one origin; automatic authority work
must prevent that split before it can be qualified.

## Consequences

- A load balancer routes every request to any ready member; Management requests cost a follower one peer round
  trip and, for now, a TLS handshake each (no pooled channel). They need the primary: while it is down they answer
  503, data-plane reads and writes do not. A forwarded request whose answer is lost may still have run, as behind
  any proxy: retry with the request's own idempotency (Management commit ids).
- Management requests that read or change a database's contents (SQL, schema, collections, indexes) run on the
  primary's copy, as they did behind a load balancer routing `/v1/management` to it: documents, collections and
  indexes written on a follower show there once client replication delivered them, and the reverse (TS-574).
- gRPC materializes a forwarded message (at most 4 MiB and 64 KiB) before the primary's in-flight check and memory
  reservation: at worst about 8 MiB per call, 64 calls per follower.
- A peer certificate can submit client-listener requests to the primary, as any client can; it gains nothing more
  than the credentials it sends, but names the region the primary answers as.
- Followers lag the primary by one stream round trip. A key issued on the primary authenticates on a follower
  once that follower applied it; a revoked key stops authenticating there at the same point.
- `Follow` serves the whole catalog (member emails, key and invitation hashes, dashboard documents) to any
  member whose certificate chains to `peer-client-ca`: use one peer CA per cluster.
- A catalog version change takes the data plane of followers down until primary and followers read the same
  version (a follower answers 503 for a catalog version it does not support, and migrates nothing). A build that
  changes the catalog version must read the previous one too; none has yet.
- Every system transaction, not only Management commits, is bounded by 262,144 changes and 64 MiB. Known writers
  that can reach it: deleting an organization with more than about 130,000 retained audit events, a usage flush of
  more than about 50,000 databases active within 10 seconds (it writes each one's totals, flush id, hour and day), and the one-time migration of a version 1 to 4 catalog
  of that size. Each needs paging into bounded transactions before a catalog that large exists (follow-up).
- The retained tail counts groups, not bytes: 1,024 groups of usage flushes of many databases can hold gigabytes.
- A follower that starts while its primary is unreachable stays not ready until the primary is back: roll one
  member at a time.
- Followers do not flush query-field counts, so index recommendations see only the primary's traffic. Their usage
  counts reach the primary (TS-581).
- A follower serves a registered database from an empty local file until client replication delivers its
  documents; for a branch or restore of a populated database that is a short window of missing documents.
- A follower that catches up with a snapshot (behind the 1,024 retained groups) misses the intents the primary already
  cleared: the files of databases deleted meanwhile stay, counted as unknown, until an operator removes them. Since
  TS-660, its sender excludes files the catalog does not register or is not creating, including backup snapshots and
  restore staging, so those files do not keep `terrabase_replication_sender_durable_lag` above 0. TS-644 also checks
  eligibility before opening a queued database file; existing backup copies stay available to restore and retention.
- A member with a catalog of its own (before 0009) refuses client replication of every database its catalog does not
  register, and a follower whose disk cannot delete a database's files stops following until it can.
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
