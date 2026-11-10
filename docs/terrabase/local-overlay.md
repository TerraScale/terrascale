# Local writes, the local overlay and the push to home (PIVOT R7)

A write the router (R4) commits in a region that is not the database's home must reach the home quickly, exactly
once and in commit order, and stay visible in its own region until the home's pages carry it. This note is the
contract for that path. The home side is [home apply](home-apply.md) (R6). Shapes (F7) build their
`provisional`, `confirmed` and `revert` states on the overlay described here ([shape overlay](shape-overlay.md)).
F8 also sends captured row images directly to other regions’ shape overlays over the origin’s bounded JetStream stream.
This asynchronous publication neither gates the write acknowledgement nor replaces the durable push to home.
A cut direct link leaves the home push and confirmed page path intact; late images are suppressed by `_ts_applied` in the pulled pages.

## Why the replica cannot hold local writes

A region's copy of a database is a page-pull replica ([region replicas](region-replicas.md)). Measured on
2026-11-04 against the pinned engine (5c168be): a transaction committed on the replica survives the next pull once,
because the sync client replays it, and **is reverted by the pull after that** whether or not the home changed the
same table. A row the replica inserted disappears and an updated row returns to the home's value. Anything stored
inside the replica file goes with it. So the replica is only a cache of local writes. The durable copy of every
unconfirmed local write lives outside it, in the push stream.

## Path of one local transaction

1. The router starts a local write transaction on the replica. Its connection captures changes into the internal
   table `_ts_local_cdc`, not into the sync client's own log, so the sync client never replays the transaction.
   The table is created with capture off, so creating it is not captured either. Measured on 2026-11-04: with this
   capture a pull reverts the local transaction without replaying it.
2. Before commit, the transaction's captured changes become one `RegionTransaction`
   ([format](home-apply.md#regiontransaction)): a new ULID id, the origin region and a commit stamp from the
   node's durable HLC (`IHlcClock`, [HLC](hlc-clock.md)). Clock admission is the router's: a node whose
   `ILocalWriteClock.Allowed` is false also refuses the local transaction here, and the write goes home.
3. Inside the still-open transaction, the node publishes the `RegionTransaction` to its own region's push stream
   with the transaction id as `Nats-Msg-Id`, then commits the replica transaction. The write is acknowledged to the
   client only after JetStream acknowledged storing the message and the commit succeeded. A publish that fails rolls
   the transaction back and the write goes home. A commit that fails after the publish answers an unknown outcome:
   the home applies the transaction and the overlay shows it locally. Commits and publishes of one database are
   serialized, so stream order is commit order.
4. The home applies the transaction (R6) and answers with an `applied` notice. With `durability=home` the client's
   acknowledgement waits for that notice instead of step 3.

The push stream is the outbox: it is durable before the acknowledgement and never held only in memory. When discovery
lists a regional partner, that node also holds the transaction before the acknowledgement. A region with no
partner does not acknowledge the write locally; the router forwards it to the home ([in-region ack](in-region-ack.md)).

## Push stream

| Item | Value |
| --- | --- |
| Stream | `TERRABASE_PUSH_<ORIGIN>` (region token upper-cased), one per region, created by that region's nodes |
| Placement | the origin region (`region:<origin>` tag) |
| Subject | `terrabase.push.<origin>.<home>.<organization>.<project>.<database>` |
| Message | one `RegionTransaction`, LZ4-compressed JSON (`Content-Encoding: lz4`) |
| Headers | `Nats-Msg-Id` (transaction id), `Tb-Hlc` (commit stamp: the 75-byte ordered `HlcTimestamp` encoding, hex) |
| Retention | limits, discard new; a full stream refuses local writes, which then go home |
| Deletion | the origin region purges a database's subject up to its oldest message the home has not confirmed |

The stream lives in the writer's region so the acknowledgement is a local round trip, and a cut link to the home
only delays delivery. JetStream drops a republished message with a known `Nats-Msg-Id`, and the home drops a known
transaction id, so nothing is applied twice. A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

## Home consumer

Each home region has one durable pull consumer `home-<home>` on every other region's push stream, filtered to
`terrabase.push.<origin>.<home>.>`.

- Messages of one database are applied one at a time, in stream order. Different databases are applied in
  parallel, with bounded concurrency.
- After `ApplyAsync` returns, whatever the state, the home acks the message and publishes the `applied` notice.
  A rejected transaction is `reverted`, so it never stalls the stream.
- An unavailable home (busy, storage fault, shutdown) does not ack. The database's later messages wait behind it, so
  order holds, and other databases continue. A stuck home-apply backoff releases its MaxParallelDatabases slot
  before the delay.
- On start, the consumer restarts from its ack floor. A redelivered message can never overtake a later one, and an
  applied one is answered from its recorded state.
- An undecodable home push is parked on stream `TERRABASE_PUSH_PARK_<home>` and then acknowledged, not terminated.

## Resuming from an applied position

A consumer's ack floor records consumption, including messages skipped while its region was not home. It does not
prove application. A node that starts consuming revisits retained messages from sequence 1, and `_ts_applied`
deduplicates transactions already held by the home. Promotion separately drains retained origin pushes before
answering success; see [origin writes across a home move](home-move-origin-replay.md).

To replay from a known applied transaction, `HomePush.ReplayFromAsync(origin, database, transactionId)` finds that
transaction's stream sequence by its `Nats-Msg-Id` on the database's subject and resets the consumer to the message
after it, but never past the ack floor + 1, because one consumer carries every database of that origin. It returns
the sequence consumption resumes at, or nothing when the stream no longer holds the transaction. Anything redelivered
that the home already applied is answered from `_ts_applied` by transaction id and changes nothing, so a replay
from any earlier position applies each transaction exactly once.

## Ordering with forwarded writes

A region's local writes and the writes it forwards home stay in the order its clients sent them (found by R13's
differential test):

- **Push-through fence.** Every write a replica region forwards home (SQL REST, and every call of a SQL WebSocket or
  Postgres-wire session) carries the last push stream sequence the region published for that database, in the
  internal `TerraBase-Push-Through: <origin> <sequence>` header or the session frame. A client cannot send it: the
  region strips it from client requests, and the home honours it only on a peer-forwarded request. Before running
  the write, the home waits until its consumer has applied that region's messages for the database through that
  sequence (a sequence at or below the consumer's ack floor is already applied). After 30 seconds it answers
  `503 unavailable` and the client retries.
- **Pull fence.** After a region forwards a write, its next local transaction on that database first pulls from the
  home, so the local transaction sees the forwarded write.

## Applied notice

Core NATS subject `terrabase.applied.<origin>.<organization>.<project>.<database>` carries the transaction id, its
state (`confirmed` or `reverted`), the home revision that includes the apply, and for `reverted` the home's row
images. A lost notice is recovered from the consumer's ack floor, which the origin reads on reconnect and every
sweep.

## The local overlay

For each database it writes locally, a node keeps the overlay: the transactions it published that the replica's
pages do not hold yet, in stream order.

- **After every pull**, the node re-applies to the replica each overlay entry whose apply is not in the pulled
  revision: not confirmed yet, or confirmed at a newer home revision than the pull reached. Entries the pull holds
  are removed and never applied again. Re-applying writes the entry's column values with triggers and
  foreign-key actions off, so it is not executed a second time.
- **Reads** on the node, including those that present a read-your-writes token, run on the replica after its
  overlay is re-applied, so they see the region's own writes. A pull and its re-apply finish before a later read
  starts.
- **Reverted entries** leave the overlay, the node pulls at once to show the home's rows, and the revert is
  surfaced: the `durability=home` waiter gets the conflict, the `terrabase.push.reverts` counter grows, and
  `terrascale.conflicts` at the home has the record.
- **Restart** rebuilds the overlay from the push stream: every message of the database after the home's ack floor.

## `durability=home`

A region commits a write locally when the request asks for the home's acknowledgement, or when its discovered regional
quorum is available ([in-region ack](in-region-ack.md)): two copies when a healthy partner is present. A region with no partner forwards the write home.

- SQL REST: send `TerraScale-Durability: home` with `/sql/execute`. When the router (R4) routes the request local, the
  region runs it on its replica, publishes it to its push stream and answers only after the `applied` notice: the
  results for `confirmed`, the home's constraint error (`unique_violation`, `foreign_key_violation`, `check_violation`,
  …, or `transaction_rolled_back`) for `reverted`, in which case the transaction is applied nowhere. The answer carries
  `TerraScale-Write-Route: …; commit=local`.
- A request without the header (or `TerraScale-Durability: local`) commits locally while the discovered regional quorum
  is healthy. A region with a partner waits for that partner's
  durable hold. A region with no partner, an unavailable quorum, a request the router sends home, and every write over SQL WebSocket and the
  Postgres wire are forwarded to the home whole.
- A refused local transaction (a shape the capture cannot carry, a busy replica, a full push stream) is forwarded home
  instead; a commit whose outcome is unknown answers `503 unavailable`.

## Metrics

On `TerraScale.TerraBase` ([telemetry](metrics-export.md)):

| Metric | Meaning |
| --- | --- |
| `terrabase.push.lag` | ms from the local commit stamp to the `applied` notice reaching the origin |
| `terrabase.push.pending` | overlay entries not yet confirmed, all databases |
| `terrabase.push.applies` | home applies by state (`confirmed`, `reverted`, `duplicate`, `unavailable`) |
| `terrabase.push.reverts` | reverted local transactions seen by the origin |

## CDC retention

The push never reads the database's CDC log after commit: each transaction is captured inside its own write
transaction and leaves the node as a stream message. So the push holds no CDC retention floor.
