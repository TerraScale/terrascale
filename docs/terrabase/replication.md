# Replication

Nodes send each other the committed document changes of client databases over gRPC with mutual
TLS. Every change group travels LZ4-compressed in a versioned application envelope. Design, wire
format and open questions: [0007](decisions/0007-lz4-application-envelope-replication.md).

## What replicates

- Document puts and deletes committed on client databases: one change group per write
  transaction, applied atomically on the receiver.
- Full mesh: every node sends the groups it originated to every address in `peer-addresses`.
- Concurrent writes converge by merge policy v1: the higher commit-offset version wins, then
  a deletion over a live document, then the greater UTF-8 JSON bytes. Applying remote versions
  advances the local commit offset so a later transaction supersedes the state it observed.
- Every write transaction on a client database is captured, whether or not `peer-addresses` is
  set, so the change log has no holes when peers are added, removed or the node restarts. Nothing
  truncates the log.
- A database missing on the receiver is created under the exact organization, project and database ULIDs in the stream. Display names never address peer storage.
- The persisted incarnation (`IDatabaseStore.Incarnation`) is the origin. Document ETags do not use it: they hash the
  replicated state under the database's organization, project and database ids, so every member answers the same ETag
  for the same state and a version read on one member satisfies `If-Match` on another (TS-574). Schema v8 retains
  `changes`, `applied` and `replication_state` alongside the document API schema and adds chained journal-prefix proofs;
  documents and tombstones stay in the existing `documents` table.
- Collections and secondary indexes replicate as documents of two reserved collections that no client can name:
  `_collections` (one document per collection, `{"created_at": …}`) and `_indexes` (one definition per index,
  `{"collection": …, "fields": […]}`). Creating writes the document and dropping writes its tombstone in the same
  captured group, so they merge like documents: the greater version wins, a drop over a create at the same version. Only
  a winning state creates or drops the collection or index on the receiver. Collections and indexes created before this
  change have no such document; their next create or drop replicates.
- A document state implies its collection, whether or not its creation replicated. Dropping a populated collection also
  captures its document deletions within the same group bounds (so a drop holds at most 1023 documents). A replicated
  drop ends the live states of the collection older than itself, as the dropping member did, removes the collection once
  nothing live remains, and refuses older incoming states afterwards; a write another member made at or after the drop
  keeps the collection. Retained journal deletions also prevent stale incoming states from reviving physically removed
  documents.

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
LZ4 application envelopes; it adds no public HTTP API. Fresh schema 11 includes two partial key indexes so
excluded empty or oversized keys do not lengthen document or retained-deletion scans.

A transient Merkle tree has 16 leaves, selected by the high nibble of SHA-256 of each length-framed
collection/id key. Each leaf hashes current states in key order, including versions, exact JSON bytes,
tombstones and collection/index metadata. Each key resolves its stored row and latest retained journal
deletion with the same version/tombstone ordering as the merge path; newer live resurrections survive.
Before hashing, each bounded page durably restores retained tombstones over missing, older or tied
live rows. This also repairs identical resurrected rows on both members, even when their roots match.
Live bodies at an unchanged merge version use the latest immutable winner in the current collection
generation's shape history, including winners imported from validated journal groups. Indexed history
seeks read sizes before bodies. Digest and range pages use these retained bytes; a repair writer rechecks
the latest winner before restoring a damaged row, then applies ordinary conflict ordering. A larger
damaged body therefore cannot poison an unchanged peer or block a legitimate equal-version winner.
Body restoration invalidates document caches and preserves existing history and the merge version.
Restoration and incoming merge share one transaction operation; queued commit or disposal waits for both.
The same live history evidence restores a row whose body alone became NULL; retained real tombstones
remain deletions. Restoration skips keys with accepted changes awaiting commit in the same transaction.
Equal or newer retained journal deletions always prevent live-body restoration.
It does not infer a canonical body from an arbitrary peer or an older losing journal entry. Missing
history, metadata bodies, changed merge versions, oversized damaged bodies, or damage to the retained
history itself lack this same-version evidence and retain the ordinary merge behavior; history is not
an independent cryptographic corruption proof. Each repair transaction bounds locally restored bodies to 2 MiB in addition to its
bounded incoming state page; overflow rolls back and subsequent digest pages restore them separately.
Materializing an intentional physical deletion and restoring a lost tombstone use the same path;
both count only rows actually changed, without appending journal groups or advancing applied frontiers.
Domain-separated binary parent hashes produce the root.
The root and leaf digests use 544 decoded bytes per shard. Equal trees transfer no states; differing
leaves select the ranges exchanged as bounded state pages. The initiating member pulls first, merges
durably, and pushes its joined states back, so one configured peer link repairs both directions.

Repair uses the engine's existing deterministic merge and JSON admission rules. Duplicate and losing
states change nothing. It does not reset, advance or manufacture a journal frontier or acknowledge a
transaction group. Database incarnations pin requests; an unregistered, deleted or replaced database
cannot be recreated by repair. Concurrent writes can make a pass stale; subsequent passes retry.
Before transferring states, each pair checks both origins against the other's applied journal prefix.
Range reads and durable merges enforce those proofs in their storage transaction; a missing, reused
or mismatched prefix fails closed, and a moving frontier retries. These pairwise proofs preserve the
direct restore fence; states do not carry transitive origin provenance, so this is not a global
quarantine mechanism for restored histories relayed through other members.
`terrabase.replication.repaired.keys` counts keys actually changed after a durable repair commit, with
no tenant, database or peer labels.

Maintenance runs one exchange at a time. Each scan page examines at most 128 bounded keys plus one
lookahead and transfers at most 128 admissible states and 2 MiB of key/body bytes;
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
read snapshot without network I/O. Oversized keys excluded by baseline replication are also
excluded from repair. Other inadmissible keys are skipped before body materialization, local
repair or hashing. Scan cursors retain the last examined key, even for empty pages, and allow at
most 63 collection bytes and 1024 id bytes so scans advance past these keys. Transferred states
retain the normal collection/id validation. The system catalog continues to use its existing primary/follower protocol.

Deletion-key enumeration seeks past repeated versions using the existing journal index, and reads
only the latest retained deletion for each key. Repeated delete/recreate cycles do not force a full
history scan for that key. This first implementation rebuilds trees from bounded pages and scans the keyspace to select ranges:
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

One member is the catalog primary; every other member sets `catalog-primary` to the primary's peer listener and
follows it. Design, limits and failover: [0009](decisions/0009-catalog-replication.md).

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

Three nodes, `a` the primary: every node sets the [peer listener](#configuration), and `b` and `c` add

```sh
TERRABASE_CATALOG_PRIMARY=https://a.internal:7443
```

Or give every member the same settings, as the orchestrator does with one workload profile, and let each find itself
by its `node-name` (the host name by default):

```sh
TERRABASE_PEERS=gru-1-db-1=10.251.0.21:9443,gru-1-db-2=10.251.0.22:9443
TERRABASE_CATALOG_PRIMARY=gru-1-db-1
```

`peers` lists every member, this one included; the others become `peer-addresses`, and the member named in
`catalog-primary` is the primary while every other follows it. A member whose `node-name` is not listed refuses to start.

Metrics on the follower:

| Prometheus name | Meaning |
| --- | --- |
| `terrabase_catalog_follower_connected` | `1` while the follower holds a stream to its primary |
| `terrabase_catalog_follower_lag` | Catalog groups the primary last reported that the follower has not applied |
| `terrabase_catalog_applied_sequence` | The primary's last group the follower applied; the highest one is the best failover candidate |
| `terrabase_catalog_follower_failures_total{reason}` | Streams that failed: `lineage` (a catalog of another origin, kept), `invalid` (a frame failed validation; nothing of it applied), `apply` (acting on applied changes, such as a database deletion, failed; retried before the next stream) or `stream` |
| `terrabase_catalog_forwarded_total{result}` | Requests the follower forwarded to its primary: `answered`, `lagging` (answered before the follower applied what it wrote, after 5 seconds) or `unavailable` (503, or a reset once the response started) |

## Configuration

| Setting | Meaning |
| --- | --- |
| `peer-port` | Inter-node gRPC listener (HTTP/2, mutual TLS); `0` picks a free port. Unset disables replication |
| `peer-certificate` | PEM leaf certificate followed by its intermediates, served and presented to peers |
| `peer-private-key` | PEM private key of `peer-certificate` |
| `peer-client-ca` | PEM CA certificate that every peer certificate must chain to, in both directions |
| `peer-addresses` | Comma-separated, distinct `https://host:port` peer listeners to send to; requires `peer-port` |
| `peers` | Every member as `name=host:port`, this node included under its `node-name`; sets `peer-addresses` to the others. Requires `peer-port` |
| `catalog-primary` | The catalog primary this node follows: a member name of `peers` (this node's own makes it the primary) or an `https://host:port` peer listener; unset makes it a primary. Requires `peer-port` |
| `write-wait-ms` | Milliseconds a client write waits for its active peers' `DurableAck` before it answers anyway, `0` to `60000`; `0` does not wait. Default `2000` |

`peer-port` and the three files are set together. The files are read at startup; an unreadable
file or any other invalid setting stops the process with exit code `2`. A node with `peer-port`
but no `peer-addresses` receives and applies groups, and captures its own writes without sending
them until `peer-addresses` is set.

Nodes exchange their configured `region` in Hello and Welcome and log both local and peer regions
after validating the handshake. Region uses the same bounded syntax as `TERRABASE_REGION` and is
descriptive node metadata; it does not authorize placement or residency. The additive Protobuf field
is optional for older peers, whose region is logged as `unknown`. It does not change database/origin
identity, journal digests or the on-disk schema.

Two nodes, `a` and `b`:

```sh
# node a
TERRABASE_REGION=gru-1
TERRABASE_PEER_PORT=7443
TERRABASE_PEER_CERTIFICATE=/etc/terrabase/peer/a.pem
TERRABASE_PEER_PRIVATE_KEY=/etc/terrabase/peer/a.key
TERRABASE_PEER_CLIENT_CA=/etc/terrabase/peer/ca.pem
TERRABASE_PEER_ADDRESSES=https://b.internal:7443

# node b: the same with b.pem and b.key, and
TERRABASE_REGION=iad-1
TERRABASE_PEER_ADDRESSES=https://a.internal:7443
```

A third node adds its address to both lists, for example
`TERRABASE_PEER_ADDRESSES=https://b.internal:7443,https://c.internal:7443` on `a`.

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

1. Sender → `Hello`: organization, project and database ULIDs (16 bytes each in network byte order), origin (as both member id and incarnation), protocol 2.0,
   format 1, LZ4 codec, limits.
2. Receiver → `Welcome` with its applied prefix of that origin and the negotiated limits: each the
   minimum of both sides. It refuses an incompatible `Hello` (a range without 1.0, no LZ4, a zero
   limit, an inconsistent identity) with a non-retryable `StreamError`, and a 17th open stream
   with retryable `STREAM_ERROR_RESOURCE_EXHAUSTED`.
3. Sender → `CommittedGroupBatch` for prefix + 1, prefix + 2, …, within the negotiated window and
   byte credit. Sender and receiver run the same check on every batch against the negotiated
   limits (gRPC message size, encoded and decoded bytes, changes, byte credit, and 2 MiB of
   collection, id and JSON bytes per group): a group that does not fit fails the stream on the
   sender before it is written, and is refused by the receiver before it is decoded.
4. Receiver, per batch: validate, apply and record the sequence in one SQLite transaction synced to disk, then
   send `DurableAck` and `AppliedAck` with the new prefix. A duplicate is acknowledged without
   being applied, after consulting the pinned store. A gap ends the stream with a retryable
   `STREAM_ERROR_INVALID_BATCH`; an invalid batch with a non-retryable one, and an unsupported
   protocol, format or codec with a
   non-retryable `STREAM_ERROR_UNSUPPORTED_FORMAT`.
5. Once every group it has is durably acknowledged, the sender completes the stream. It fails
   closed on a `Welcome` that is not exactly an answer to its `Hello`: protocol 2.0, event and
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
silently dropped connection. If a peer reports a prefix beyond the database's own change log (the
file went back in time, for example a restore from a snapshot), the sender sends nothing for that
database and keeps logging the failure: new groups would reuse numbers the peer already applied.

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

A node sends the current state of a database as baseline change groups when its log does not cover
that state: a copy (branch) and a populated database upgraded from an earlier schema without a journal. The baseline
is appended when the node first opens the database, one bounded group per transaction, before the
log is marked complete; a copy also gets a new incarnation. Applying is idempotent, so a baseline repeated after a crash costs only bytes.
Pre-existing states that fail the shared JSON contract (including the 1 MiB document limit), key
contract, or merge-version bound are left out of the baseline and counted in a warning naming the
database. Their stored data remains readable locally; valid states and later writes still replicate.
Initialization is coordinated per database, so its baseline scan does not block unrelated database
creation, opening or deletion. The store is published only after seeding completes. Deletion of that
database waits for initialization; shutdown cancels initializers and waits within its configured grace.
Cleanup continues after grace expires, retaining directory ownership until initialization and all
sessions have ended. A canceled initiating open or copy closes its unpublished store before returning;
an already-published file can be seeded again on a later open or explicitly deleted.
Copies materialize retained physical deletions as tombstones on the destination before clearing the copied journal,
so the new baseline preserves deletion merge evidence. Local document mutations and replicated
applies use separate transactions; mixing them faults the transaction even for a no-op call.

Replication calls the same storage admission path as local writes, including losing incoming
states. Legacy rows or journal entries outside this contract are rejected on apply rather than
silently normalized. Such files require operator repair/export into a fresh database before
replication; this PR provides no automatic JSON conversion.
Imported winners also append shape history atomically with the applied frontier, retain their
original document versions, and wake live readers. Shape history offsets are local commit positions,
independent of sender journal sequences (see `shape-reads.md`).

## Limits and timeouts

| Limit | Value |
| --- | --- |
| gRPC message, either direction | 4 MiB + 64 KiB |
| Envelope; decoded change group | 4 MiB each |
| Captured write transaction | 1024 changes; 2 MiB of UTF-8 collection, id and JSON bytes |
| In flight per stream | 8 batches; 16 MiB of envelopes (lowered to the peer's limits) |
| Received change group | the negotiated limits; 2 MiB of collection, id and JSON bytes |
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
| `terrabase_replication_divergence_detections_total{reason}` | Journal conflict detections across all databases, including omitted ones; reason is `peer_ahead`, `welcome_digest` or `ack_digest` |
| `terrabase_replication_peer_lag{peer}` | Outstanding durable groups, including every database |
| `terrabase_replication_database_lag{peer,database}` | Outstanding durable groups for a database slot |
| `terrabase_replication_database_diverged{peer,database}` | 1 after detecting a conflicting journal digest or a peer prefix beyond the local head; clears only once a validated prefix reaches the latest head |
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
- The member that accepted a write keeps decision 0003's `synchronous = NORMAL`: a power loss can roll its file back
  behind what its peers applied, and that database then stops replicating from it until an operator recovers it.
- No SQL write path exists yet; a future SQL write through `IStorageTransaction` is captured as
  one group.

## Schema compatibility

Main's schema v6 births/high indexes are preserved; replication capture and separate merge versions
are v7, and journal-prefix proofs are v8. Released main schemas upgrade transactionally. Earlier
unmerged PR revisions used conflicting v6/v7 numbers and are not a supported upgrade source;
export their data and start with fresh files rather than changing `user_version` manually.
