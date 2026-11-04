# Local writes, the local overlay and the push to home (PIVOT R7)

A write the router (R4) commits in a region that is not the database's home must reach the home quickly, exactly
once and in commit order, and stay visible in its own region until the home's pages carry it. This note is the
contract for that path. The home side is [home apply](home-apply.md) (R6). Shapes (F7) build their
`provisional`, `confirmed` and `revert` states on the overlay described here.

## Why the replica cannot hold local writes

A region's copy of a database is a page-pull replica ([region replicas](region-replicas.md)). Measured on
2026-11-04 against the pinned Turso (5c168be): a transaction committed on the replica survives the next pull once,
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

The push stream is the outbox: it is durable before the acknowledgement and never held only in memory. The region's
second node also holds the transaction before the acknowledgement, so it is on two nodes ([in-region ack](in-region-ack.md),
D16).

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
transaction id, so nothing is applied twice.

## Home consumer

Each home region has one durable pull consumer `home-<home>` on every other region's push stream, filtered to
`terrabase.push.<origin>.<home>.>`.

- Messages of one database are applied one at a time, in stream order. Different databases are applied in
  parallel, with bounded concurrency.
- After `ApplyAsync` returns, whatever the state, the home acks the message and publishes the `applied` notice.
  A rejected transaction is `reverted`, so it never stalls the stream.
- An unavailable home (busy, storage fault, shutdown) does not ack. The database's later messages wait behind it, so
  order holds, and other databases continue.
- On start, the consumer restarts from its ack floor. A redelivered message can never overtake a later one, and an
  applied one is answered from its recorded state.

## Resuming from an applied position

A home consumer's position is its ack floor: every message at or below it is applied (or answered as already applied)
and acknowledged. A node that starts consuming, including a new home after a failover (R8), resets the durable
consumer to the ack floor + 1, so no message is skipped and none overtakes an earlier one.

To replay from a known applied transaction, `HomePush.ReplayFromAsync(origin, database, transactionId)` finds that
transaction's stream sequence by its `Nats-Msg-Id` on the database's subject and resets the consumer to the message
after it, but never past the ack floor + 1, because one consumer carries every database of that origin. It returns
the sequence consumption resumes at, or nothing when the stream no longer holds the transaction. Anything redelivered
that the home already applied is answered from `_ts_applied` by transaction id and changes nothing, so a replay
from any earlier position applies each transaction exactly once.

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

A local write can ask for the home's acknowledgement: `"durability": "home"` on SQL REST and SQL WebSocket requests,
and `SET terrascale.durability = 'home'` on the Postgres wire. The answer then waits for the `applied` notice of
the transaction. `confirmed` answers success. `reverted` answers the conflict, and the transaction is not applied
anywhere. A write that goes home anyway (an unsafe shape, or a database homed in this region) is already
home-durable.

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
