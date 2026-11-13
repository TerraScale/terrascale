# Home apply (PIVOT R6)

A database's home applies transactions that committed in another region (D4, D5). This is TerraBase's own apply:
the engine's push and its server-side apply are never used (D14). R7 (streaming push over JetStream) delivers transactions
to this API; until R7 lands, the internal HTTP endpoint below is the only producer.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

## Guarantees

- **All or nothing per transaction.** Every change of a transaction is applied in one write transaction at the home,
  or none is.
- **A rejected transaction never blocks the stream.** A transaction the home cannot apply is rolled back, recorded as
  `reverted` with its changes in `terrascale.conflicts`, and the call succeeds. Only an unavailable home (busy,
  storage fault, shutting down) answers an error, and the same transaction can be sent again.
- **Exactly once.** The transaction id is the dedupe key. Sending an applied or reverted transaction again returns its
  recorded state and changes nothing.
- **Column conflict resolution by HLC (D21).** Each column of each row keeps the stamp of its last write: the writer's
  [`HlcTimestamp`](hlc-clock.md), ordered by milliseconds, logical counter, then node id (ordinal) as the tie-breaker. A change to a column applies only when its stamp is greater.
  A losing column is not applied and is recorded in `terrascale.conflicts` as `superseded`, with its value.
- **Delete tombstones.** A delete keeps a tombstone with its stamp, so an older insert or update of that row cannot
  bring it back (`superseded`). A delete older than a column write of the row loses (`superseded`), and the row stays.
- **Inserts never overwrite.** An insert of a key that exists at the home is a `primary_key` conflict and reverts its
  transaction (D12: tables that take local writes use ULID keys). An update of a row that the home never had reverts
  as `missing_row`.
- **Clock trust.** A node issues a stamp for its own write only while its clock is [trusted](hlc-clock.md#clock-trust):
  a replica that is refused forwards the write home, and a home that is refused answers `409 serialization_conflict` with
  `Retry-After: 1` and commits nothing. Applying a received transaction uses the stamp it carries and works whatever the receiver's clock.
  Receiving an otherwise valid transaction with a future timestamp does not cause a clock conflict. Observation limits
  the remote contribution to local UTC plus 50 ms and emits a warning and counter when it clips a timestamp; the
  effect retains its original stamp.
- **Triggers do not fire for applied changes.** The origin region already ran them and their writes are part of the
  transaction. See [triggers](#triggers).
- **Foreign keys are re-checked at the end of the transaction.** Foreign-key actions (`CASCADE`, `SET NULL`) do not run
  for applied changes; instead every applied child row must reference an existing parent and no applied delete or key
  change may leave a referencing row behind. A violation reverts the transaction (`foreign_key`).
- **Home writes take part.** A write that commits at the home (home-region clients, forwarded transactions) stamps the
  columns it changed with the home's HLC, so a staler change from another region cannot overwrite it.

Mixed versions: the 50 ms budget bounds a stamp against its origin's clock only. A receiver's own clock can be off
by up to the same budget in the other direction, so a receiver can see a valid stamp up to about 100 ms ahead of its
clock; it clips only its observation of that stamp, never the stamp the effect keeps. A receiver on a build older than
this rule can still revert such a write for its timestamp. Roll every region to this build without a long hold.

A committed transaction with any superseded changes increments `terrabase_home_apply_superseded_total{region}` once, labelled only by its origin region; duplicate delivery does not increment it.

## Write states

| State | Meaning |
| --- | --- |
| `provisional` | Committed in its region; the home has not applied it yet. The home answers this for an unknown id. |
| `confirmed` | Applied at the home. Some columns may have lost to newer writes (`superseded` rows in `terrascale.conflicts`). |
| `reverted` | Rejected at the home; none of its changes are applied. The origin region must revert its rows to the home's images. |

## C# API (in-process, for R7)

`IHomeApply` in `TerraBase.Features.HomeApply`:

```csharp
ValueTask<HomeApplyResult?> ApplyAsync(TenantContext database, RegionTransaction transaction, StreamSequence? position, CancellationToken cancellationToken);
ValueTask<HomeApplyResult?> StateAsync(TenantContext database, string transactionId, CancellationToken cancellationToken);
```

`ApplyAsync` runs on the database's home only, inside the organization's QoS context (E11). It throws only when the
home is unavailable (`StorageBusyException`, a busy `SqlStatementException`, cancellation, shutdown) and returns null
when the database is not stored on this node; every data outcome is a result. `position` is the stream identity and
sequence of a pushed message (null for a forwarded write); its frontier commits in the same engine transaction as
the effects and the result. A position at or below the frontier applies nothing and returns the recorded result, or
`provisional` when none is kept (the sequencer publishes no notice for that). Validate the transaction as
`ApplyEndpoint.Invalid` does before calling it. Only the stream sequencer (below) calls it for pushed messages, with the
next message after the frontier that the stream returns for the database: positions are sparse, so the sequencer
detects a purged gap only through the origin's purge marker (below), never from the positions themselves. A stuck
apply backoff releases the parallel slot before the delay.

## Internal HTTP

Internal listener only, behind the peer credential; 404 on the public listener.

- `POST /v1/sync/organizations/{organization}/projects/{project}/databases/{database}/apply[?position=<sequence>]`:
  body is one `RegionTransaction`, at most 8 MiB, sent with `Content-Length`. Its caller is an in-region partner
  finishing a lost node's held transaction, which its origin had already stored in its push stream before the hold;
  `position` is that stream sequence (a partner of the previous build sends no position). The home never applies the
  body here: it wakes the sequencer of the transaction's origin and database, waits up to 10 seconds, and answers `200`
  with the recorded `HomeApplyResult` once the transaction is decided, by its id or because the database's frontier
  for the origin's current stream reached `position`. Otherwise it answers `503` and the partner keeps its hold and
  retries. Without a position only the recorded result counts. `404` when the database is not homed here or its
  placement does not name the origin region; `400` for a malformed transaction or position.
- `GET …/databases/{database}/apply/{transaction}`: the transaction's `HomeApplyResult`, or
  `{"id":…,"state":"provisional"}` when the home has not seen it (or applied it more than 7 days ago).

### RegionTransaction

```json
{
  "id": "01JC3Y6Q4M2V8K5T7W9X1Z3B5D",
  "region": "sjc",
  "hlc": { "physicalMilliseconds": 1793805256123, "logical": 0, "nodeId": "sjc-1" },
  "changes": [
    { "op": "insert", "table": "users", "values": { "id": "01JC…", "email": "a@example.com", "credits": 5 } },
    { "op": "update", "table": "users", "key": { "id": "01JC…" }, "values": { "credits": 7 } },
    { "op": "delete", "table": "sessions", "key": { "id": "01JC…" } }
  ]
}
```

- `id`: a ULID minted by the origin region for the transaction.
- `region`: the origin region; it must be in the database's placement and differ from the home.
- `hlc`: the transaction's commit `HlcTimestamp` on the origin node (R12, [hlc-clock.md](hlc-clock.md)). Every change of
  the transaction carries this stamp; a replay keeps it.
- `changes`: 1 to 1024, in commit order. `table` is a table of the database with a declared `PRIMARY KEY` (D12: ULID
  keys); `key` names every primary-key column; `insert` carries every column of the new row, `update` only the
  columns it changed (never a key column), `delete` only the key.
- Values are SQL values as in `/sql/execute`: `null`, an integer, a number with a fraction or exponent for a real,
  a string, or `{"base64": …}` for a blob. Send integral reals with a fraction (`2.0`).

### HomeApplyResult

```json
{
  "id": "01JC3Y6Q4M2V8K5T7W9X1Z3B5D",
  "state": "reverted",
  "conflicts": [
    { "kind": "unique", "table": "users", "key": { "id": "01JC…" }, "message": "UNIQUE constraint failed: users.email" }
  ],
  "rows": [
    { "table": "users", "key": { "id": "01JC…" }, "values": null }
  ]
}
```

- `state`: `confirmed` or `reverted` (or `provisional` when the historical result is unknown).
- `conflicts`: what `terrascale.conflicts` recorded for this transaction.
- `rows`: for `reverted` only, the home's current image of every row the transaction touched (`values: null` when the
  row does not exist at the home), so the origin region and its shape overlay can revert to it (D9 step 3).

## `terrascale.conflicts`

Every database has a read-only `terrascale.conflicts` table, readable with SQL on every surface (REST, WebSocket,
Postgres wire) in every region. One record per losing column of a `superseded` change, one per row for a row-level
loss (a delete, or a write against a newer delete), and one per `reverted` transaction. The winner is the write the
home kept; the loser is the region transaction that lost.

| Column | Type | Meaning |
| --- | --- | --- |
| `id` | TEXT | ULID of the record, in record order. |
| `kind` | TEXT | `superseded`, `unique`, `primary_key`, `not_null`, `check`, `foreign_key`, `constraint`, `missing_row`, `schema`; `clock` may remain from before this release until retention removes it. |
| `table_name` | TEXT | The table, or null when the rejection is not about one row. |
| `row_key` | TEXT | JSON object of the row's primary key, or null. |
| `column_name` | TEXT | The losing column, or null for a row-level loss and for `reverted`. |
| `winner_value` | TEXT | JSON of the value the home kept: the column's value, the row (`{"column": value, …}`), or null when the row is deleted or unknown. |
| `loser_value` | TEXT | JSON of the losing value: the column's value, the row's changed values, null for a losing delete, or the whole transaction's changes for `reverted`. |
| `winner_region` | TEXT | Region of the kept write, or null when no write of the row is recorded. |
| `winner_node` | TEXT | Node that stamped the kept write. |
| `winner_hlc` | TEXT | HLC of the kept write. |
| `winner_transaction_id` | TEXT | ULID of the kept write's transaction (a region transaction, or a home transaction's id). |
| `winner_state` | TEXT | `confirmed`, or null with no winner. |
| `loser_region` | TEXT | Region the losing transaction committed in. |
| `loser_node` | TEXT | Node that stamped it. |
| `loser_hlc` | TEXT | Its commit HLC. |
| `loser_transaction_id` | TEXT | Its id. |
| `loser_state` | TEXT | `superseded` (the transaction applied, this part lost) or `reverted` (nothing applied). |
| `message` | TEXT | Why, without engine names. |
| `created_at` | TEXT | When the home recorded it (UTC, ISO 8601). |

- **Values.** A value whose JSON is longer than 4 KiB is recorded as `{"sha256": "<hex of its JSON>", "bytes": <length>}`
  instead, so `terrascale.conflicts` never holds a copy of a large value.
- **HLCs** are fixed width and sort in HLC order: 12 hex digits of Unix milliseconds, `.`, 8 hex digits of the logical
  counter, `:`, the node id (`019a50c1e2f3.00000000:sjc-1`).
- **Retention.** The home keeps at most the newest 10,000 records per database, none older than 30 days; each
  transaction that records a conflict trims the rest.

```sql
SELECT loser_transaction_id, kind, table_name, row_key, column_name, winner_value, loser_value
FROM terrascale.conflicts WHERE loser_state = 'reverted' ORDER BY id DESC LIMIT 20;
```

Writes to it are refused with `sql_sandbox`.

## Triggers

A trigger fires once, where its write commits. Every `CREATE TRIGGER` runs with an extra condition that is true for
every customer statement and false while the home applies a region transaction, so applied rows (which already hold
the trigger's own writes from the origin) do not fire it again. Schema reads (`sqlite_schema`) show the trigger as it
was written.

## Storage

Per database, internal and invisible to customers, created when the database is created with replica regions, gains
them, or first applies a transaction: `_ts_rows` (per column and per tombstone: the write's HLC, region and transaction id), `_ts_applied` (each applied or
reverted transaction with its result, kept 7 days, so a transaction is exactly once within that window) and
_ts_conflicts. Stream delivery also maintains the internal _ts_frontier table, keyed by the JetStream stream
name and creation time. Its sequence advances with the effects and result in the same engine transaction.
The frontier is a high-water mark: every message of the database on that stream up to it has been decided at the
home. A position at or below it returns its recorded result when present and `provisional` otherwise (the result is
kept seven days), and applies nothing. JetStream sequence numbers cover all databases in a stream, so positions need
not be consecutive. The frontier is written only by the sequencer that applied the positions below it, and it moves
with the database file when the home moves. A database whose file has no frontier for a stream (one written before this
release, or a new stream identity) starts at the stream's first retained message and skips what it already recorded;
frontiers are not rebuilt for messages the stream no longer holds.

A database without them stamps nothing and reads an empty `terrascale.conflicts`. Rows written at the
home before the database had replica regions carry no stamp and lose to any region write, which can only be newer.
The tables are created in place with no format change: older files gain them on first use.

## Stream delivery and held databases

The home reads each origin region's push stream through one durable consumer, named after the writer epoch that
created it (`home-<region>-epoch-<n>`). The origin tells its client a write
succeeded before the home applies it, so every pushed message that may carry a write is owed to a customer: it is
never dropped or skipped by a decision of the home. The exceptions are listed below: messages that cannot carry such
a write are parked, and a database whose next message the origin purged before this copy applied it is held for an
operator (`purged`).

For each origin stream and database there is one sequencer on the home's writer node. It reads the database's
frontier for that stream, asks the stream for the next message after it that matches the database (for every
addressed home), applies that message, and advances the frontier to its position in the same commit; then it asks
again. A delivery from the consumer only wakes the sequencer and tells it which positions to read ahead (up to 32
reads at once); a delivered message is never applied because it was delivered. Acknowledging a delivery therefore
decides nothing: a node that is not the database's home acknowledges its deliveries without applying them, and
whatever it acknowledged is still read from the stream by whichever node becomes the home. Within one origin stream a
database's retained messages apply in stream order, and the sequencer never moves past a purge the database's copy did
not reach (below). There is no order between different origin streams.

The sequencer's identity leaves out the home a message is addressed to: after a home move the origin stream holds
earlier messages addressed to the former home and later ones addressed to the current home, and the sequencer applies
them in stream order. A message that cannot apply is retried with backoff from 100 ms, doubling up to a 60 second
ceiling, with no attempt limit, on a timer of its own: it needs no delivery to try again. Its database is held: later
messages of that database on that stream wait behind it. Other databases keep applying while they are not held
themselves and while the home's 16 shared apply slots and the stream are available; a held sequencer releases its slot
during backoff. A held database writes nothing to `terrascale.conflicts`, because nothing was decided against the
customer.

A sequencer that is not held keeps at most 32 deliveries unacknowledged, so it can publish a replayed result again
after a restart; later deliveries are acknowledged at once and stay in the stream until the sequencer reads them. A
held sequencer acknowledges every delivery at once, so held databases never fill the consumer's 4,096 in-flight
messages and never stop deliveries to healthy databases of the same origin. Each round of reads checks afterwards that
the stream still has the identity the run started with, that the stream's last sequence is not below the frontier,
and, at the home, that the origin's purge marker for the database is not ahead of the frontier.

When a node becomes a database's home (a move or a failover), it starts the sequencer for every origin stream from the
database's frontier and waits up to 30 seconds for each to reach the end of its stream before it answers the promotion.
When that wait runs out it still answers promoted: it counts `terrabase.placement.move.catch_up_timeouts` and logs, per
origin, the database, its frontier and the stream's last sequence, and the sequencer keeps going in stream order after
the answer. Direct writes at the new home are accepted from the moment it answers, also before its sequencers caught
up, so a constraint can then be decided against a direct write that is newer than a pushed write still in the stream:
an already acknowledged pushed write can become `reverted` where stream order would have confirmed it. Two nodes can only both run a
sequencer for one database while each believes it holds the home: every apply checks the home lease when it commits,
and a commit after a lost lease fails and is retried by the node that now holds it.

Each origin is consumed in runs. A run starts only while this node holds the writer lease: it deletes every consumer
of its own or an older writer epoch on that origin stream and creates a fresh one for its epoch, which delivers from
stream sequence 1. A node never deletes or reads a consumer of a newer epoch; it waits until that consumer is gone or
it learns the newer owner. Deleting a consumer ends the pull requests still waiting on it, and its unacknowledged
deliveries cannot be acknowledged or redelivered into the fresh consumer. A run ends at once when the writer lease is
lost or lapses, when the consumer fails, when the origin stream was recreated, or when a sequencer ends unexpectedly
while it still has work. A renewal before the lease lapses keeps the run; a renewal after a lapse, also on the same
node, is a new lease and starts a new run. Ending a run stops every sequencer of that origin and waits for them before
the next run opens. The next run redelivers every retained message; a redelivered message at or below the frontier
publishes its recorded result again (nothing when no result is kept) and commits nothing, and the sequencer continues
from the frontier.

A message leaves an origin stream only in two ways, both done by the origin on its own stream. The origin purges a
database's messages up to a position once its copy of the database, pulled from the home, holds a recorded result for
every pending transaction up to that position (or the home's applied notice reported it reverted, which it publishes
only after recording it), and never past that copy's frontier for the stream; this is the home's decision, not a
consumer acknowledgement. Before that purge it records the position in the same stream as the purge marker
`terrabase.push-state.<origin>.purged.<organization>.<project>.<database>`, written with an expected-last-sequence check
so it only moves forward, and it keeps only the newest marker. The marker lives in the stream it describes, so every
node that can read the messages can read it, it disappears only with the stream (a new identity, whose frontier starts
again), and a stream restored from an older snapshot is caught by its last sequence (`stream_behind`). When a node
deletes a database, it purges that database's messages and marker from its own region's stream at once; messages of
the deleted database still in other regions' streams are parked as `deleted_database` by the home. The stream has no
age limit and refuses new messages when full instead of dropping old ones. While the home runs the previous build its
copies hold no frontier, so the origin does not purge them until the home runs this build.

| Kind (retry `reason`) | What happens | What the stream sees |
| --- | --- | --- |
| Applies (confirmed or reverted by the merge) | Effects, result and frontier commit in one engine transaction, then the applied notice is published | Acknowledged after the commit |
| Applied before (a replay at or below the frontier) | The recorded result is published again, nothing when none is kept; no effects | Acknowledged |
| The catalog names another home | Not applied here; the home reads the message from the stream | Acknowledged |
| Catalog unreadable (`catalog`) | Retried; database held | Acknowledged at once |
| No catalog record and no deletion record (`unknown_database`) | Retried; database held | Acknowledged at once |
| Payload cannot be opened or decoded (`undecodable`), data key or object-store read fails (`object_store`), cryptographic failure (`crypto`) | Retried; database held | Acknowledged at once |
| SQL engine failure without one of the final codes below, including read-only, busy, I/O, "too many open files", and a table or column the engine reports missing while the statement runs (`engine`); cancellation or timeout (`cancelled`, `io`); disposal during shutdown (`disposed`); broker failure (`broker`); database file not on this node (`not_stored`); anything else (`other`) | Retried; database held | Acknowledged at once |
| Subject names no database (`malformed`) | Parked | Acknowledged after the park is stored |
| Empty payload (`empty`) | Parked | Acknowledged after the park is stored |
| The catalog holds a deletion record for the database (`deleted_database`) | Parked | Acknowledged after the park is stored |
| More than 32 messages of one database in flight | Read from the stream by the sequencer, in order | Acknowledged at once |
| Stream read or acknowledgement fails (`broker`, `io`, `cancelled`) | Retried; database held | Acknowledged at once |
| The origin's purge marker for the database is ahead of this copy's frontier (`purged`) | Held for an operator; nothing more of the database applies from that stream | Acknowledged at once |
| The stream's last sequence is below the frontier (`stream_behind`) | Held for an operator; nothing applies from that stream | Acknowledged at once |
| A message the previous build may have seen, older than the 7 day result retention, with no recorded result (`upgrade_ambiguous`) | Held for an operator | Acknowledged at once |
| The origin stream was recreated | The consumer run ends | The next run reads the new stream from sequence 1 under its new identity |
| Writer lease lost or lapsed (a renewal after a lapse is a new lease) | The consumer run ends at once; every sequencer of that origin stops and nothing more is dispatched or negatively acknowledged | The next run deletes the consumer and starts a fresh one at sequence 1; deliveries outstanding on the old consumer die with it |

Only messages that cannot carry a write still owed to anyone are parked. A park stores the received payload, origin,
stream identity, sequence, database, reason and UTC time in `TERRABASE_PUSH_PARKED_<HOME>` before the message is
acknowledged. That stream never expires or evicts a record: it holds at most 1 GiB, and when it is full a new park is
refused, so the message stays held and retried (`io`) until an operator makes room. Its storage policy is the origin
stream's (single replica, file).

`TERRABASE_PUSH_PARK_<HOME>` holds records parked by earlier releases (the raw payload under
`terrabase.push-park.<home>.<origin>.<organization>.<project>.<database>.<sequence>`). This release never writes,
reconfigures or purges it; its record count is reported as `parked_records{reason="previous_layout"}`, and a warning
names the count whenever it changes.

Applies record a permanent outcome only for a property of the message against the database at the home, never for a
condition of this node. These outcomes rely on today's routing, which sends schema changes, inserts, foreign-key
changes and parent deletes to the home rather than through an origin stream; a change to that routing has to revisit them.

| Outcome | Condition | Can it pass by itself? |
| --- | --- | --- |
| `reverted` (`unique`, `primary_key`, `not_null`, `check`, `foreign_key`, `constraint`) | The engine refuses the statement with one of the final codes: SQLite primary code 19 (`SQLITE_CONSTRAINT`, and the engine's native constraint status, which it maps to 19), 18 (`SQLITE_TOOBIG`) or 20 (`SQLITE_MISMATCH`). The message text never decides it: a failure without one of these codes holds the database and retries (`engine`) | No: the rows it conflicts with are already at the home |
| `reverted` (`missing_row`) | An update of a row with neither a row nor a tombstone at the home | No: an origin sees a row only from the home or from its own earlier write, and that write applies earlier on the same origin stream |
| `reverted` (`schema`) | Shape validation before any statement runs: the change names a table, column or primary-key shape the home does not have, or is not an insert, update or delete | No: schema changes run only at the home, and origins learn the schema from it |
| `superseded` (kept in a confirmed result) | A newer stamp at the home wins the column or row | No: the merge decides it by stamps |
| Parked (`malformed`, `empty`, `deleted_database`) | The subject names no database, the payload is empty, or the catalog holds the database's deletion record | No |

A node whose own clock is not trusted refuses to issue stamps for its own writes; applying a received transaction never
asks for a stamp, and observing a remote stamp only clips it, so clock trust never decides an apply. A remote stamp
that names no node fails the observation and holds the database (`other`). Every engine failure without a final code
holds the database and retries, and is never recorded as the transaction's result.

What an operator does for each hold reason (the warning log line names the database, origin, sequence and reason):

| Reason | Action |
| --- | --- |
| `catalog`, `broker`, `io`, `cancelled`, `disposed`, `object_store`, `crypto`, `not_stored`, `engine`, `other` | Nothing while it passes by itself; if it lasts, fix the named dependency (catalog, broker, object store, data keys, disk, engine). |
| `unknown_database` | Check whether the database was created and its catalog record reached this region; a database that never existed is deleted through the deletion job, after which its messages park. |
| `undecodable` | Check the data key of the named encryption id; the message applies once it can be opened. |
| `purged` | The database's copy at this home is behind what was acknowledged: the origin purged messages this copy never applied. Restore the database from the copy that applied them (the former home, a backup) or accept the loss explicitly; there is no automatic repair. |
| `stream_behind` | The origin's stream was restored from an older state under the same identity. Compare the stream with the frontier and recreate or repair the stream; nothing applies until the stream's last sequence reaches the frontier again. |
| `upgrade_ambiguous` | The previous build may have applied the message and its result has expired. Read the message's effects against the database and skip or republish it by hand. |

To recover a parked record there is no command; the procedure is manual. Read the record with
`nats stream get TERRABASE_PUSH_PARKED_<HOME> <sequence>`: a JSON record whose `payload` field holds the received
payload in base64 (in the previous-layout stream the message is the raw payload). Before republishing, check that the database exists and is not deleted, that the catalog names this
region as its home, and that the record's reason no longer holds (an empty payload or a subject without a database
cannot be repaired by republishing). The payload is encrypted, so the transaction id inside it is visible only after
decrypting it with the database's data key; if it can be read, `GET /v1/sync/organizations/<organization>/projects/<project>/databases/<database>/apply/<transaction>`
on the internal listener answers `provisional` when no result is recorded. A transaction with a recorded result
returns that result instead of applying again, but only while the result is kept (seven days). Then publish the payload to
`terrabase.push.<origin>.<home>.<organization>.<project>.<database>`. The republished message gets a new stream
sequence after everything the origin published since, so it does not restore the original order: it applies after
later writes of the same database, and the merge decides each column by its stamp. The record stays in the parking
stream until an operator removes it.

What an operator sees:

- `terrabase.push.held` by `reason`: origin databases held behind a message that has not applied yet.
- `terrabase.push.holds` by `reason`: each time a database becomes held or its hold reason changes.
- `terrabase.push.held_age` (seconds): time since the oldest held message first failed.
- `terrabase.push.apply_retries` by `reason`: each failed apply or stream read attempt by the kinds above.
- `terrabase.push.parked` and `terrabase.push.parked_records` by reason: newly parked and retained parked records.
- One warning log line when a database becomes held, one when its hold reason changes and one when it is released, each with the database id, the
  origin, the stream sequence and the kind, never payload content. Each newly parked message emits one log line.

No metric carries a per-database label. The alert rules for these metrics live with the monitoring configuration.

## Upgrade from the previous build

The previous build consumed each origin stream with one durable consumer per home region (`home-<region>`), decided
messages per database in parallel and acknowledged them out of order across databases. Before this build first
consumes an origin stream it reads, once, the acknowledgement floor of every such consumer on that stream (the position
up to which that consumer had decided everything) and records the lowest of them with the stream's last sequence as
`terrabase.push-state.<origin>.upgrade` in that stream, written only if no record exists. Each later region raises the
record's last sequence when it upgrades. Only after the record is stored does a node delete its own region's
`home-<region>` consumer. A database with no frontier on that stream starts at the recorded floor, so a database can
move to another node before its own frontier is written and still start there. The lowest floor is used because a
message is decided only when the consumer of its home at that time acknowledged it, and every consumer saw every
message.

Messages above the floor are read from the stream as usual and deduplicated by transaction id. Up to the recorded last
sequence the previous build may have seen them, so two cases differ: a message it parked as undecodable (found in a
`TERRABASE_PUSH_PARK_<HOME>` stream) is skipped as decided, and a message whose transaction id is older than the
7 day result retention less an hour, with no recorded result, holds the database (`upgrade_ambiguous`) instead of
applying a second time. A message for a database the catalog does not know still holds (`unknown_database`), where the
previous build dropped it.

Rollback: the previous build recreates `home-<region>` with delivery from sequence 1 and deduplicates by transaction id,
so it applies nothing twice whose result is younger than 7 days. A message retained longer than that after it was
applied would apply again; the origin purges a message once a pulled copy shows it decided, so before a rollback check
that no origin stream holds a message older than 7 days (`nats stream info TERRABASE_PUSH_<REGION>`, first message
time). The previous build ignores the `terrabase.push-state.*` subjects and keeps writing and reading the `Tb-Epoch`
header, which this build still writes for it.

## Known gaps

- While an origin runs the previous build it purges without a purge marker, and while a home runs the previous build
  its copies hold no frontier; the `purged` check covers only purges made by this build.

- Tombstones in `_ts_rows` are kept until the row's key is written again.
- Applied writes are metered where they committed (the origin region), not again at the home.
