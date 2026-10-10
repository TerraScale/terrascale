# Replication

Nodes send each other the committed document changes of client databases over gRPC with mutual
TLS. Every change group travels LZ4-compressed in a versioned application envelope. Design, wire
format and open questions: [0007](decisions/0007-lz4-application-envelope-replication.md).

## What replicates

- Document puts and deletes committed on client databases: one change group per write
  transaction, applied atomically on the receiver.
- Full mesh: every node sends the groups it originated to every address in `peer-addresses`.
- Every document is a register ([anna-core](design/anna-core.md), Documents): a local write mints a dot under the
  member's replica id, stamps it with the member's hybrid clock and replaces every value the member knows. A receiver
  joins each delta into the stored register, so delivery order, duplicates and retries cannot change the result. Reads
  take the register's last-writer-wins write (the greatest stamp, then replica id), whose stamp is the document's
  version; concurrent writes stay in the register as siblings until a write replaces them. A dot that arrives with a
  second value (a replica that minted it twice) fails the group instead of losing either write.
- Every write transaction on a client database is captured, whether or not `peer-addresses` is
  set, so the change log has no holes when peers are added, removed or the node restarts. Nothing
  truncates the log.
- A database missing on the receiver is created under the exact organization, project and database ULIDs in the stream. Display names never address peer storage.
- A stream's origin is the member's replica id (little-endian) and the last 8 bytes of the database file's incarnation
  (`IDatabaseStore.Incarnation`, a ULID whose first bytes are its creation time), so an unclean start (a new epoch) or a
  copy starts a new origin and a sequence never names two different groups. Document ETags do not use it: they hash the
  last-writer-wins write under the database's organization, project and database ids, so every member answers the same
  ETag for the same state and a version read on one member satisfies `If-Match` on another (TS-574). A client database
  keeps each document's register in `documents`, its own stream in `changes` (deltas) and `journal` (dependency
  vectors), and the applied position of every other origin in `applied`. Applying a group of a member's later epoch
  removes the positions of its earlier epochs on the same file: the later stream starts with a baseline of everything
  that member held, so `applied` keeps one row per member and file instead of one per unclean start.
- Collections and secondary indexes replicate as documents of two reserved collections that no client can name:
  `_collections` (one document per collection, `{"created_at": …}`) and `_indexes` (one definition per index,
  `{"collection": …, "fields": […]}`). Creating writes the document and dropping writes its tombstone in the same
  captured group, so they join like documents: the last-writer-wins write creates or drops the collection or index on
  the receiver.
- A live document implies its collection, whether or not its creation replicated. Dropping a populated collection writes
  a tombstone over each live document in the same group (so a drop holds at most 1023 documents). A replicated drop
  removes the collection once nothing in it is live; a document another member wrote concurrently, at a later stamp,
  keeps it. The registers stay with their tombstones, so a late delta joins them instead of bringing a document back.
- A client database removes documents only with tombstones; `DeleteAsync` and prefix deletes are the system database's.

Storage enforces one JSON admission contract for client Put/PATCH and replication Apply: a complete
UTF-8 object without duplicate properties, at most 1 MiB, 16 container levels and 4096 values, with
the document API numeric limits. Rejected writes fault their transaction; client transactions report
the operation index. Shapes embed admitted JSON without reparsing it.

## Merkle anti-entropy (TS-678)

Every member with a peer listener reconciles registered client databases with the current cell members and
explicit `peer-addresses`, waiting 5 seconds after each completed pass. A slow or failed pass still
leaves this idle gap; unchanged journal heads cannot reveal physical state corruption.
Database-wide placement currently has one shard (zero).
Invalid membership peer addresses are logged without their values and skipped; a failed peer does
not stop reconciliation with the remaining members or configured peers.
The closed-beta `terrabase.alpha.AntiEntropy` gRPC contract uses the existing mutual-TLS listener and
LZ4 application envelopes; it adds no public HTTP API. The client schema includes a partial key index so excluded
empty or oversized keys do not lengthen register scans.

A transient Merkle tree has 16 leaves, selected by the high nibble of SHA-256 of each length-framed
collection/id key. Each leaf hashes the stored registers in key order: each key and its register's canonical encoding
(every value with its dot and stamp, and the causal context), collection, index and uniqueness metadata included.
Domain-separated binary parent hashes produce the root. The root and leaf digests use 544 decoded bytes per shard.
Equal trees transfer no states; differing leaves select the ranges exchanged as bounded pages of registers
(`DeltaGroup` without dependencies). The initiating member pulls first, joins durably, and pushes its joined registers
back, so one configured peer link repairs both directions.

Repair joins registers exactly as replication applies deltas, under the same JSON admission: a register already held
changes nothing, a stale one cannot replace a newer write, and a dot that arrives with a second value fails the page.
It does not reset, advance or manufacture a journal position or acknowledge a transaction group. Database
incarnations pin requests; an unregistered, deleted or replaced database cannot be recreated by repair. Concurrent
writes can make a pass stale; subsequent passes retry. No journal proof fences repair: joins are idempotent and an
origin never reuses a sequence (a copied, imported or restored file starts a new origin), so a page of older registers
changes nothing it would not change through replication. Digests hash registers, while reads, queries and SQL use each
row's `version` and `body`: building a digest checks every row against its register's last-writer-wins write (the JSON
kept aside for a hidden uniqueness loser) and rejoins the register of a damaged row in a durable transaction, which
rewrites the row, its shape history and cached reads. A join that leaves the register unchanged also rewrites a row that
no longer reads as it.
`terrabase.replication.repaired.keys` counts keys whose register changed after a durable repair commit, with no tenant,
database or peer labels.

Maintenance runs one exchange at a time. Each scan page examines at most 128 bounded keys plus one
lookahead and transfers at most 128 admissible registers: the first of at most 3 MiB and all of them of at most 2 MiB of
keys and encoded states;
each exchange or incoming call reserves 16 MiB until its buffers have been consumed. Incoming calls
are limited to 16. Each range read or merge has a fresh 15-second progress budget; an exchange can
continue beyond 15 seconds while its pages make progress. Local and remote digest construction each
have a separate five-minute budget, with a fresh 15-second budget for each local scan/repair page.
An independent 15-minute budget caps the complete per-database exchange, including both digests and
both page loops. Progress cannot renew it; expiry releases maintenance to the next database or peer,
and a later pass retries remaining work.
Continuation cursors must advance in SQLite key order. A fresh 15-second transmission budget
remains active after each handler returns; expiry aborts an unread response stream.
Unary responses are explicitly flushed before application completion, keeping blocked
writes inside admission. Reservations remain held through application response completion
or cancellation; this does not establish peer consumption or final network delivery. Reads return
their SQLite sessions before network waits, except the local digest scan, which holds a consistent
read snapshot without network I/O. Empty and oversized keys stay out of the partial index and out of
repair. Other inadmissible keys are skipped before their registers are decoded or hashed. Scan cursors retain the last examined key, even for empty pages, and allow at
most 63 collection bytes and 1024 id bytes so scans advance past these keys. Transferred states
retain the normal collection/id validation. The system catalog continues to use its existing primary/follower protocol.

A deleted document is one register with its tombstone, so repeated delete/recreate cycles add no keys to scan. This
first implementation rebuilds trees from bounded pages and scans the keyspace to select ranges:
CPU work is O(keys) per pass even when trees match. A shard that cannot complete within the deadline
needs incremental persisted tree maintenance; this implementation does not claim bounded convergence
time under continuous writes, memory pressure or an oversized shard.

## Write-wait

A member answers a client write on a client database (documents, transactions, collection create and drop) only once
every active peer durably acknowledged it, so a read on any member then sees it: read-your-writes across members.
Design, guarantees and limits: [0010](decisions/0010-write-wait-and-synced-durable-ack.md).

- The wait starts after the write transaction ended and lasts at most `write-wait-ms` (2 s by default). On expiry the
  member answers the committed write as usual and counts a timeout; it never turns a committed write into an error.
- A peer whose stream for the database is failing (unreachable, restarting) is not waited for until a stream to it
  succeeds again.
- Receivers sync the WAL (`synchronous = FULL`) before `DurableAck`, so an acknowledged group survives the peer's power
  loss; local commits stay `NORMAL` ([0003](decisions/0003-sqlite-synchronous-normal.md)).

The management catalog and API keys (`_system.db`) replicate separately, from one catalog primary to its
followers: see [Catalog](#catalog). Client replication does not create or rename organizations, projects or their
registered databases. There is no pre-tenancy directory migration; main's tenant layout and exclusive ownership
checks apply.

## Catalog

The current runtime uses one catalog primary and followers identified by the
legacy `catalog-primary` input. Automatic member and authority discovery is
required by TS-773; there is no per-member production setup step. Design, limits and failover: [0009](decisions/0009-catalog-replication.md).

- Only the primary writes the catalog. A follower forwards every authenticated Management API request on its client
  listener (reads too), and a data-plane creation of a database it does not have, to the primary over
  `CatalogForwarding.Forward`; the primary authenticates and serves them through its own pipeline, answering as the
  follower's region. The follower answers once it applied what the request wrote (at most 5 seconds later), so a key
  or database created through any member serves there at once: no load balancer routing to the primary is needed.
  A primary that cannot be reached answers `503 catalog_primary_unavailable` with `Retry-After: 1`. The
  administrative listener is never forwarded: on a follower its catalog writes (`POST /admin/api-keys`, the
  service credential's Management writes) answer `503 not_primary`. A follower runs no catalog writer of its own
  (intent recovery, bootstrap, backups, usage flushes); it sends its usage counts to the primary every 10 seconds
  over `CatalogUsage.Add`, numbered per process, which the primary adds once per sequence.
- A follower authenticates API keys and resolves organizations, projects and databases from its replica, and
  serves a registered database before any of its documents arrived (an empty local file that client replication
  fills).
- A deleted database goes from every follower: a follower deletes its local files when it applies the deletion's
  cleanup intent (a failure stops it from following until the deletion succeeds), and every member receives client
  replication only for databases its catalog registers (`STREAM_ERROR_UNAUTHORIZED`, retryable, otherwise), so no
  peer brings a deleted one back. A follower never deletes a file only because it is unregistered; once caught up it
  counts such files in `terrabase_storage_unknown_files`, as a primary does at startup.
- A member sends client replication only for databases its catalog registers or is creating (a branch's baseline is
  written before its registration commits), as receivers accept only registered ones: backup snapshots, restore staging
  and files the catalog no longer names stay where they are and do not count in
  `terrabase_replication_sender_durable_lag`.
- `/readyz` of a follower answers 503 until it has applied the primary's head once in its process, and while its
  primary reports a head it has not applied yet; a primary that cannot be reached leaves the last head, so a
  caught-up follower stays ready while the primary restarts. Before stopping a member, a drain waits for its
  `terrabase_replication_sender_durable_lag` to reach 0 (decision 0009 explains why readiness does not).
- A follower takes a snapshot only into an empty catalog or one of the primary's origin; it never replaces a
  catalog of another origin.

Membership and the catalog authority must be discovered automatically. The
existing `peers`, `peer-addresses` and `catalog-primary` inputs are legacy
implementation dependencies; their manual profile recipes are retired. A
mismatched identity must still fail validation rather than adopt another catalog.

Metrics on the follower:

| Prometheus name | Meaning |
| --- | --- |
| `terrabase_catalog_follower_connected` | `1` while the follower holds a stream to its primary |
| `terrabase_catalog_follower_lag` | Catalog groups the primary last reported that the follower has not applied |
| `terrabase_catalog_applied_sequence` | The primary's last group the follower applied; the highest one is the best failover candidate |
| `terrabase_catalog_follower_failures_total{reason}` | Streams that failed: `lineage` (a catalog of another origin, kept), `invalid` (a frame failed validation; nothing of it applied), `apply` (acting on applied changes, such as a database deletion, failed; retried before the next stream) or `stream` |
| `terrabase_catalog_forwarded_total{result}` | Requests the follower forwarded to its primary: `answered`, `lagging` (answered before the follower applied what it wrote, after 5 seconds) or `unavailable` (503, or a reset once the response started) |

## Automatic replication contract

Private NetBird membership admits nodes and discovers every serving region.
OCI Vault provisions and delivers peer mutual-TLS credentials. Peer destinations,
local region and catalog authority are derived automatically; write-wait and
resource limits have one correct setting in code.

**Current gap (TS-773):** the runtime still requires peer listener, certificate,
address and catalog options. This documentation does not implement automatic
replication. Preserve TLS chain/EKU checks, registered-database authorization,
journal identity validation and secret-free startup errors during removal.

Hello/Welcome validates bounded region metadata. Older peers may omit the field
and remain `unknown`; it does not authorize placement or residency.

## Certificates

- One certificate per node is both the peer listener's server certificate and the sender's client
  certificate, so it needs both TLS usages: server authentication (`1.3.6.1.5.5.7.3.1`) and
  client authentication (`1.3.6.1.5.5.7.3.2`).
- A name in the certificate must match the host other nodes use in `peer-addresses`.
- Every certificate in `peer-client-ca` is a trust root, in both directions, so a CA rotation can
  list the old and the new CA until every node has its new certificate. `peer-certificate` contains
  the leaf first, followed by its intermediate chain. Both listeners and senders present the complete
  chain using an offline TLS certificate context. Supplied intermediates aid chain building; only
  configured CAs are trust roots. Revocation checks and certificate issuer downloads are disabled
  during startup and TLS validation; certificates cannot direct the node to an issuer URL.
- At startup the certificate must chain to `peer-client-ca` for both usages, or the process exits
  with code 2.
- Rotation: replace the files. TLS handshakes check SHA-256 content fingerprints at most once per monotonic
  second and reload changed files; no restart. Certificate selection and trust validation use one captured pair
  for the whole handshake. Open connections keep their certificates until they
  reconnect. Until the new files can be read and the certificate chains to the new authorities
  for both usages (files replaced one at a time), the node keeps the previous pair, logs a warning
  and retries on the next eligible handshake. Concurrent handshakes bypass an in-progress
  reload. Each PEM read is bounded to 1 MiB; at most 64 trust roots and 64 intermediates are imported. Connection leases
  release native certificate handles after the host or replacement releases ownership and
  the last connection closes.

## Sync stream

One `Replication.Sync` stream carries one database from one origin to one peer.

1. Sender → `Hello`: organization, project and database ULIDs (16 bytes each in network byte order), origin (as both member id and incarnation), protocol 1.0,
   format 1 (register deltas, `DeltaGroup`), LZ4 codec, limits. These numbers stay 1 ([anna-core](design/anna-core.md),
   decision 9): members of another format never meet, because a format change ships at a cutover that stops every
   member before any starts the new build.
2. Receiver → `Welcome` with its applied prefix of that origin and the negotiated limits: each the
   minimum of both sides. It refuses an incompatible `Hello` (a range without 1.0, no LZ4, a zero
   limit, an inconsistent identity) with a non-retryable `StreamError`, and a 17th open stream
   with retryable `STREAM_ERROR_RESOURCE_EXHAUSTED`.
3. Sender → `CommittedGroupBatch` for prefix + 1, prefix + 2, …, within the negotiated window and
   byte credit. Sender and receiver run the same check on every batch against the negotiated
   limits (gRPC message size, encoded and decoded bytes, changes, byte credit, and 3 MiB of
   collection, id and register bytes per group): a group that does not fit fails the stream on the
   sender before it is written, and is refused by the receiver before it is decoded.
4. Receiver, per batch: validate, apply and record the sequence in one SQLite transaction synced to disk, then
   send `DurableAck` and `AppliedAck` with the new prefix. A duplicate is acknowledged without
   being applied, after consulting the pinned store. A gap ends the stream with a retryable
   `STREAM_ERROR_INVALID_BATCH`; an invalid batch with a non-retryable one, and an unsupported
   protocol, format or codec with a
   non-retryable `STREAM_ERROR_UNSUPPORTED_FORMAT`.
5. Once every group it has is durably acknowledged, the sender completes the stream. It fails
   closed on a `Welcome` that is not exactly an answer to its `Hello`: protocol 1.0, event and
   record format 1, merge policy 1, LZ4, this database in this epoch, and limits equal to the
   minimum of both sides (a peer may lower a limit, never raise one). Before releasing anything,
   an acknowledgment must name the peer from `Welcome`,
   epoch 0 with an empty configuration id (until membership, TS-469), this database and origin,
   and a prefix between the last one and the last group sent. Only a valid `Welcome` and an
   acknowledgment that advances the durable prefix extend the progress deadline.

The sender visits every client database at startup and every database with a new captured
commit, one database at a time per peer. Every 5 s it also queues the known client databases for
frontier reconciliation, coalescing pending work by incarnation. A caught-up peer that loses data
is repaired from its next `Welcome` without requiring another local write. Sender heads, dirty
work and progress are keyed by organization, project, database and incarnation; deletion removes them, and a replacement
receiver incarnation replaces its predecessor's prefix. Receiving streams pin their store and
end with `FailedPrecondition` when it is retired; duplicates always consult that store's journal.
A turn captures a fixed journal head, then materializes one bounded group per short read transaction. Every transaction ends before network writes or
acknowledgment waits, so WAL checkpoints can proceed. It sends at most 256 groups from that fixed
head before yielding, so a database written without pause still yields to the next one. A
database that fails backs off independently; new commits to healthy databases can wake the peer worker
during that backoff. Failed databases are retried, retryable error or not, and the next stream resumes
from the peer's `Welcome`. The sender keeps one response read active while filling the window, so
validated advancing ACKs extend its progress deadline even during a blocked request write. HTTP/2 pings (every 10 s, 5 s timeout) close a
silently dropped connection. An origin never reuses a sequence, so a peer that reports a prefix beyond the origin's head
holds groups it never committed (a database file replaced by hand under the same member and epoch): the sender sends
nothing for that database, counts `peer_ahead` and keeps logging the failure.

### Handoff during a peer outage (TS-679)

The accepting member's persisted `changes` journal holds writes a peer missed, including
complete transactions and tombstones. Startup discovery replays that backlog after a sender
restart, so a returning peer can catch up without another client write. The sender's dirty set
only schedules attempts; it is not the durable copy of pending writes.

This first TS-679 slice tests outage and restart replay through the real listeners. Total
pending journal storage is not bounded yet, and point reads do not contact peers to repair
stale state. Those remain subsequent slices; the per-group and in-flight bounds above do not
bound the disk backlog. Recovery requires the originating member's journal to remain available.

## Baselines

A database's stream starts again, on a new origin, with a baseline when its journal does not cover the database for this
member: a copy (branch or import, which also gets a new incarnation) or an unclean start of the member (a new epoch,
[anna-core](design/anna-core.md), Identities and dots). The journal of the old origin is dropped, and every register,
live or tombstone, is appended as change groups when the node first opens the database, one bounded group per
transaction (a register larger than 2 MiB alone in its group), before the journal is marked complete. Joins are
idempotent, so a baseline repeated after a crash costs only bytes. Initialization is coordinated per database, so its baseline scan does not block unrelated database
creation, opening or deletion. The store is published only after seeding completes. Deletion of that
database waits for initialization; shutdown cancels initializers and waits within its configured grace.
Cleanup continues after grace expires, retaining directory ownership until initialization and all
sessions have ended. A canceled initiating open or copy closes its unpublished store before returning;
an already-published file can be seeded again on a later open or explicitly deleted. Local document mutations and
replicated applies use separate transactions; mixing them faults the transaction even for a no-op call.

Replication applies the storage admission of local writes to every write a delta holds, losing ones included.
Applied writes that change what a reader sees append shape history atomically with the applied position, keep their
own stamps as versions, and wake live readers. Shape history offsets are local commit positions, independent of sender
journal sequences (see `shape-reads.md`).

## Limits and timeouts

| Limit | Value |
| --- | --- |
| gRPC message, either direction | 4 MiB + 64 KiB |
| Envelope; decoded change group | 4 MiB each |
| Captured write transaction | 1024 changes; 2 MiB of UTF-8 collection, id and encoded register bytes |
| In flight per stream | 8 batches; 16 MiB of envelopes (lowered to the peer's limits) |
| Received change group | the negotiated limits; 3 MiB of collection, id and register bytes, 2 MiB once it holds more than one change |
| Open `Sync` streams per node | 16 |
| Receiver idle timeout | 30 s waiting for a request, or for a response write the peer does not read |
| Sender connect and progress timeout | 15 s to connect, to get `Welcome`, and between acknowledgments that advance the durable prefix |
| Frontier reconciliation | Every 5 s discovers persisted client databases and retries failed opens; pending requests coalesce |
| Retry backoff | Per database incarnation: 100 ms doubling to 10 s, full jitter, reset after a successful sync |

Startup discovery isolates database open, bootstrap, baseline and head-read failures. Peer workers run
alongside the initial scan, so healthy commits do not wait for the whole scan. Each database open
and head read shares one 15-second discovery budget; incomplete discovery is retried on reconciliation. Healthy
stores continue replicating; the next reconciliation retries failed discovery without a separate
retry queue. An enumeration error also retries on the timer. `sender_initialized` remains zero
until one discovery pass measures every persisted database successfully.

A read transaction enables SQLite `query_only` and refuses mutations through the engine interface.
Its connection is reset after the read ends, before pooling. A write that exceeds capture bounds
fails before copying the excess change; the faulted transaction cannot commit. The document
transaction API reports `503 capacity` with the failing operation index; a collection drop that
exceeds the same bounds reports `503 capacity` and preserves the entire collection.

## Metrics

Counters on the `TerraScale.TerraBase` meter, scraped at `/metrics` on the administrative
listener. The OpenTelemetry Prometheus exporter maps instrument names and units to these names,
for example `terrabase.replication.sent.plain` with unit `By` to
`terrabase_replication_sent_plain_bytes_total`. A host reports only the instruments it created
through its own `IMeterFactory` plus process-wide ones (runtime), so several hosts in one process
(the integration suites) do not mix their counts.

| Prometheus name | Meaning |
| --- | --- |
| `terrabase_replication_sent_groups_total` | Change groups sent to peers |
| `terrabase_replication_sent_plain_bytes_total` | Change group bytes sent, before LZ4 |
| `terrabase_replication_sent_encoded_bytes_total` | Envelope bytes sent, after LZ4 |
| `terrabase_replication_received_groups_total{result}` | Groups received; `result` is `applied`, `duplicate` or `rejected` |
| `terrabase_replication_sender_discovery_passes_total` | Completed journal discovery passes, including passes with failures; useful with initialized state to identify stalled discovery |
| `terrabase_replication_sender_initialized` | `0` while persisted journal heads are being measured, `1` once all existing databases are initialized |
| `terrabase_replication_write_wait_seconds{result}` | Histogram of the time client writes waited for their peers' `DurableAck`; `result` is `acknowledged` (every peer acknowledged), `degraded` (answered without a peer whose stream is failing) or `timeout` (answered at `write-wait-ms`) |
| `terrabase_replication_sender_durable_lag` | Committed groups some peer has not durably acknowledged, summed over peers and databases; `0` means the last validated prefixes acknowledge every group; receiver state loss is discovered by periodic reconciliation. It counts a commit before the commit returns, and a database's older groups before network progress; require `terrabase_replication_sender_initialized = 1` before interpreting zero lag |


## Replication status and alerts

`GET /replication` is an unauthenticated **loopback administrative** endpoint with
`Cache-Control: no-store`. The client and peer listeners return 404. It reports
whether the peer listener is enabled, whether journal discovery is initialized,
the number of tracked outbound database incarnations, omitted database detail, and each
configured outbound peer's durable lag and database observations. A receive-only
node has no outbound peers. This endpoint never reads document bodies or opens
storage; it snapshots the sender's existing state.

New Prometheus instruments:

| Prometheus name | Meaning |
| --- | --- |
| `terrabase_replication_divergence_detections_total{reason}` | Journal conflict detections across all databases, including omitted ones; reason is `peer_ahead` |
| `terrabase_replication_peer_lag{peer}` | Outstanding durable groups, including every database |
| `terrabase_replication_database_lag{peer,database}` | Outstanding durable groups for a database slot |
| `terrabase_replication_database_diverged{peer,database}` | 1 after detecting a peer prefix beyond the local head; clears only once a validated prefix reaches the latest head |
| `terrabase_replication_database_catchup_seconds{peer,database}` | Monotonic age since observing an unacknowledged backlog, including retry time; 0 when no catch-up is timed |
| `terrabase_replication_convergence_seconds{peer,database}` | Histogram of completed catch-ups, ending when a valid Welcome or DurableAck covers the latest local head |
| `terrabase_replication_observability_omitted` | Database incarnations excluded from bounded detail; aggregate and per-peer lag still include them |

`peer` is the zero-based position in configured `peer-addresses`. `database` is a
reusable numeric slot from 0 through 127, assigned to the first 128 observed local
database incarnations. The endpoint maps slots to organization, project, database
and incarnation IDs. Tenant names, IDs, origins and peer URLs never become metric
labels. Slots are released on deletion and available to later discovery. Previously
allocated slots emit zero when unused to retire stale exporter samples; never-used
slots are omitted. A slot's histogram can include earlier occupants; use the endpoint
to identify its current occupant.
Unresolved journal conflicts survive omission and slot promotion; only a validated
prefix covering the latest local head clears them, or deletion retires the incarnation.
Peer labels are bounded by operator configuration, database labels by 128, so
database churn cannot create new time series. If more than 128 databases exist,
`omittedDatabases` and the omitted gauge explicitly identify incomplete detail;
do not interpret zero divergence as evidence for omitted databases.

Catch-up starts when a commit or discovery reveals backlog, or an attempt fails
after invalidating a previously verified prefix. It survives reconnects and
additional writes until the latest head is durably acknowledged. Discovery of
persisted backlog measures from discovery, not from the original write time.
Ordinary periodic prefix validation can temporarily raise lag without starting a
catch-up timer. Process restart and database deletion reset observations. These
are local outbound measurements, **not** end-to-end request latency or a proof of
whole-database document equality. Zero divergence means no journal conflict has
been detected for observed slots; unreachable peers are represented by lag and
catch-up age. Require `terrabase_replication_sender_initialized == 1` before
interpreting zero lag.

Initial alert thresholds (tune to the deployment's write rate and latency budget):

- Warning: `terrabase_replication_peer_lag > 0` for 60 seconds, gated on
  `terrabase_replication_sender_initialized == 1`.
- Critical: `terrabase_replication_database_catchup_seconds > 300` for 30 seconds.
- Critical: `terrabase_replication_database_diverged == 1` for 30 seconds, or
  `increase(terrabase_replication_divergence_detections_total[5m]) > 0` to include
  conflicts in omitted databases. Stop treating that database's replication as
  healthy; inspect both journals and use
  the existing restore recovery procedure. Never overwrite a serving cell.
- Warning: `terrabase_replication_sender_initialized == 0` for 60 seconds, or
  `terrabase_replication_observability_omitted > 0` for 60 seconds.
- Warning: per-peer catch-up p95 above 30 seconds over 15 minutes, with at least
  10 completed episodes: `histogram_quantile(0.95, sum by (instance, le, peer)
  (rate(terrabase_replication_convergence_seconds_bucket[15m]))) > 30`, gated on
  `sum by (instance, peer) (increase(terrabase_replication_convergence_seconds_count[15m])) >= 10`.
  These aggregations retain the scrape target's `instance` label to separate nodes.
  Uncompleted episodes require the catch-up-age alert above.

The ts-dashboard admin panel is a separate ticket; no dashboard code or external
persistence is introduced here.

## Not yet

- Not interoperable with terrabase-rust (different change group format and merge policy).
- `changes` and document tombstones are never truncated; retention needs membership to know all peers.
- Database delete is not replicated.
- Read-your-writes across members holds only for writes answered within `write-wait-ms` while every peer was active
  ([0010](decisions/0010-write-wait-and-synced-durable-ack.md)): after a timeout, or on a peer that was failing or is
  catching up after a restart, a read can miss a write and a conditional write there fails with 412 or 404 until it
  arrives.
- A baseline is the whole current state, written into the change log in bounded chunks: it costs as much
  disk as the data it covers.
- The member that accepted a write keeps decision 0003's `synchronous = NORMAL`: a power loss can lose its last commits
  locally. Its unclean start raises its epoch, so it never mints their dots again and its streams continue on new origins,
  and the peers keep what they acknowledged; the member itself gets those writes back only when they are written again,
  until refill after an expired lease ([anna-core](design/anna-core.md) §5.3) covers it.
- A database file replaced by hand under the same member and epoch is detected only when a peer is ahead of its head;
  restore through import, which starts a new origin.
- No SQL write path exists yet; a future SQL write through `IStorageTransaction` is captured as
  one group.

## Schema compatibility

Every database file is schema version 1, and the number never increases (user rule, 2026-10-09). There are no
upgrades: a file of any other version is refused. A data directory also records its layout and storage format in
`.terrabase.lock`: the layout byte, then the SHA-256 of the bootstrap schemas. A build refuses a directory that any other
format recorded before it opens a file and exits with code 2, so a rollback cannot rewrite newer data; builds from before
the record accept only the layout byte, so they refuse a directory a newer build wrote.

A format change wipes storage at a cutover, never at startup (COS decision 2026-10-10). Builds of different formats share
protocol version 1 and cannot refuse each other, so the operator stops every member in every region first, verifies the
targets, deletes them and lists the deleted database ids; each member then bootstraps fresh storage when it starts. A
build that wiped on start would do it member by member during a rolling deploy, next to members still running the other
format. Never change `user_version` or the lock file manually.
Client and system databases bootstrap different tables under that one number: client documents are registers (TS-647
step 4c), and the system database keeps its catalog journal until TS-647 step 5.
