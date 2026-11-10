# Provisional, confirmed and revert on shapes (PIVOT F7/F8)

A reader in a replica region sees local writes at once and other regions’ writes after direct NATS delivery. Each local transaction ([local overlay](local-overlay.md))
reaches replica regions’ live shape tails as `provisional`. The home's pages later mark it `confirmed`, or `revert` when the
home rejected it. Provisional data is never sealed, mirrored or cached.

## Confirmed and revert: from the home's pages

The home records every applied region transaction in `_ts_applied` within the same commit as its row changes
([home apply](home-apply.md)). Every shape chunk, at the home and in a replica, adds the region transactions that a commit
settles to that commit's entry:

```json
{"offset":"12","ops":[{"op":"insert","key":"41","row":{"id":"01JC…","v":"sjc"}}],"confirmed":["01JC3Y6Q4M2V8K5T7W9X1Z3B5D"]}
{"offset":"14","ops":[],"revert":["01JC3Y7A0B1C2D3E4F5G6H7J8K"]}
```

- `confirmed` lists transactions applied at the home in this commit. The commit's ops show what the home kept, including
  columns that lost to newer writes.
- `revert` lists transactions the home rejected. None of their changes are applied, so the commit may have no ops.
- A commit that settles a transaction always has an entry, even if it changed nothing in the shape: the client must drop
  the provisional copy either way.

These fields are offset-addressed like the rest of a chunk, so identical URLs still return identical bytes.

## Provisional: from the region

A live request on a replica sends `local=<digest>`. The digest is `0`, or the `local` value of its previous live
response. When the shape is up to date, the answer adds:

```json
"provisional":[{"id":"01JC3Y6Q4M2V8K5T7W9X1Z3B5D","ops":[{"op":"insert","key":"~01JC3Y6Q4M2V8K5T7W9X1Z3B5D.17","row":{"id":"01JC…","v":"sjc"}}]}],
"local":"9f2c41d07a3be851"
```

- `provisional` is every pending transaction of the region that changes this shape (local commits, and forwarded writes
  below, and direct changes from other regions), in arrival order. It is the
  complete set at `next_offset`: the client replaces its provisional layer with it. At most 256 transactions or 1 MiB.
- `local` is the first 16 hex characters of SHA-256 over the listed ids joined by newlines, or `0` for none.
  `TB-Shape-Local` carries the same value.
- At the head, a live request answers as soon as the set differs from `local`, and otherwise waits for a commit as before.
  A local commit on the same node wakes waiting tails at once; other nodes and replica regions subscribe directly to
  `terrabase.provisional.*.*.*.*`. The writer publishes to
  `terrabase.provisional.<origin>.<organization>.<project>.<database>` with its transaction id as `Nats-Msg-Id`.
  `TERRABASE_PROVISIONAL_<ORIGIN>` retains those subjects in the origin region, using one file-backed replica,
  64 MiB, a 1 MiB message limit, ten-minute retention/deduplication, and discard-old retention.
  Its row images are sealed as TBP1 under the database envelope key before publication; receiving regions decrypt
  only while custody permits it. Database erasure also purges that database's provisional subject and pending rows.
  The sealed subject namespace prevents raw local notices from entering the retained stream during a rolling update.
  Live subscribers receive the publication in one hop without a pull through home or waiting for the storage ack.
  The provisional publisher has eight pending messages, each at most 1 MiB of captured rows; the wire limit includes
  TBP1's 78-byte overhead. An overflow falls
  back to home delivery. The subscription queue also holds at most eight messages.
- Direct delivery is best effort: disconnected readers do not replay the provisional stream. R7’s durable push and
  the normal home-confirmed page path continue independently. On reconnect, duplicate ids are suppressed in the
  pending set, and `_ts_applied` in the pulled pages suppresses late confirmed or rejected images before projection.
  A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
- Ops use the shape's filter and columns, with move-in and move-out. A row a pending transaction inserted is keyed
  `~<transaction>.<row id>` until the home confirms it, because its home row id is not known yet. Later pending changes
  to that row use the same key; changes to home rows resolve the home row id by primary key. If a receiver missed
  the insert, an update of a row absent from its home pages uses a provisional key, and a delete of an absent row
  is omitted. A writer-local row id never identifies an unrelated home row after a stream gap.
- Live responses that carry `local` are `Cache-Control: private, no-store`.
- `local` requires `live=true` and is `0` or 16 lowercase hex characters (`400 invalid_request` otherwise). The home
  region has no pending local writes and ignores it.

Hint sockets send `{"type":"provisional","handle":…,"offset":…,"next_offset":…,"local":…}` when the region's pending
set for the shape changes without a new commit. The client then makes a live request with its own `local`.

## Forwarded writes

A REST write that a replica region forwards to the home ([write routing](sql/write-routing.md)) is already committed
when its answer returns. The home's answer to the region carries that commit's rows, with the home's row ids, in the
internal header `X-TerraBase-Commit-Rows` (at most 48 KiB; larger commits skip this). The region strips the header before it
answers the client. It adds the commit to the pending set as one provisional transaction with a new id, keyed by the home's
row ids. The pending set drops it once the replica's head passes that commit. That commit carries no `confirmed`
marker: the next response with `provisional` no longer lists it. SQL WebSocket and Postgres wire writes reach the tails
after the next pull.

## Client rule

Keep two layers. Confirmed rows follow the apply rule of [shape reads](shape-reads.md). The provisional layer is the last
`provisional` list. On each response, apply the chunk's ops to the confirmed rows and drop every provisional
transaction named in `confirmed` or `revert`. If the response has `provisional`, replace the layer with it. The visible
rows are the confirmed rows with each provisional transaction's ops applied in order. `@terrascale/sdk` does this and
reports the keys shown from provisional writes in `state.provisional`.

## Limits

- The region keeps at most 1024 pending transactions per database and 64 MiB per node, each for at most 10 minutes. A
  transaction outside these limits, or a NATS message over 1 MiB, shows once the home confirms it.
- A restarted node starts with no provisional transactions. Its SQL reads still see them through the overlay.

## Proof

`tests/TerraBase.Slow/Shapes/ShapeOverlayTests.cs` (multi-host JetStream drill, beside `HomePushTests`): a local write wakes a parked sjc tail with its provisional insert, then
the pulled home pages confirm it and the provisional set empties; a write the home rejects turns into `revert` and
the home's row.

F8: `tests/TerraBase.Slow/Shapes/RegionShapeStreamTests.cs` proves sjc → fra provisional delivery before home apply, confirmation/rejection, and home-only fallback during an isolated TCP link cut without duplicate rows.
