# 24. Log chunks: change groups through the object store (RT3b, FR-50)

Base: the `Push` stream of [8. Transport](08-transport.md) and [23. Protocol version](23-protocol-version-and-pipelining.md).

Code:
- `Features/Replication/LogChunks.cs`: the format and the reading side.
- `Features/Replication/LogChunks.Shipping.cs`: the sending side.
- Hooks in `PeerStreams` (sender) and `PeerStreamReceiver`.

## What it does

A sender ships each peer the change groups after that peer's ack.

- When two or more peer machines still need the same stretch of the log, and one copy in the object store costs less
  than a send to each, the sender seals that stretch once as a **log chunk** and puts it in the bucket.
- Each peer then gets only a reference (`Batch.chunk`, with an empty `groups`). It reads the chunk from the bucket, opens
  it and applies it like an inline batch.
- Everything else stays inline: small batches, live single writes, and a catch-up that only one peer needs.

### Cost rule

The rule is a code constant, using list prices from 2026-10-23:

- R2: a PUT (class A) costs $4.50 per million and a GET (class B) $0.36 per million. Egress and deletes are free.
- OCI outbound transfer past the free 10 TB a month: $0.025/GB from South America (`gru`), $0.0085/GB elsewhere.

With `n` reading machines and egress price `e` per byte, a chunk of `S` sealed bytes is cheaper than `n` inline sends when
`(n - 1) · S · e > PUT + n · GET`. This needs `n ≥ 2`; with one reader a chunk never pays. For `n = 2`, the break-even is
about 209 KB from `gru` and 614 KB from `sjc` or `fra`.

### Which groups make a chunk

1. A group that alone reaches the two-reader break-even is its own chunk.
2. Smaller groups go in **buckets** of change ids: power-of-two ranges sized to hold one to two times the break-even at the
   page's shipped bytes per change id.
   - A database copy keeps its bucket width while pages stay within a factor of four of it.
   - So every link maps a group to the same chunk, whatever ack it resumes from.
   - A link that resumes inside a bucket sends the rest of that bucket inline, and is aligned from the next one.
   - A bucket that the page does not complete is sent inline.

### Readers and sharing

- **Readers.** The sender counts the peer machines whose latest ack (from their acks and batches in the last minute) is
  before the chunk's first change id. Only peers whose `Welcome` reads chunks count.
- **One decision per chunk.** The first link to reach a chunk decides it for every link: each chunk is put once.
- **Reuse.** A chunk already put is read by every later link whatever the count, because one GET costs less than a send.

### Sealing, keys and failure

- **Format.** `TBL1` ‖ 12-byte nonce ‖ AES-256-GCM ciphertext ‖ 16-byte tag, with the object key as associated data.
  - Every chunk has its own random key (`LogChunk.seal`). Only the peer stream carries it, and that stream already
    carries the plaintext of every inline batch.
  - The bucket holds only ciphertext. A changed or swapped object fails authentication.
  - `LogChunk.decoded` bounds the receiver's memory admission before it reads the object.
- **Keys and cleanup.** Keys are `v1/replication/<machine>/<ULID>`.
  - A chunk is kept for 10 minutes, then forgotten and its object deleted.
  - Once an hour, the sender deletes its own objects that it no longer knows and that are older than an hour (left by an
    earlier process).
  - At most 256 chunks are known at once; past that, batches go inline.
- **Failure.**
  - A receiver that cannot read or open a chunk refuses the batch with `Ack.chunk_unread`. The sender then sends that
    peer's batches inline for one minute.
  - A failed PUT sends the batch inline.
- **Bucket.** The platform bucket of continuous backup (`IObjectStore`, settings `backup-*`).
  - Moving it from Tigris to R2 is a configuration change only.
  - A machine without a bucket reads and sends no chunks.

## Rolling deploy

Additive proto fields only:

- `Welcome.chunks` (3);
- `Batch.chunk` (5) and the `LogChunk` message;
- `Ack.chunk_unread` (7).

A receiver says in its `Welcome` that it reads chunks. Older builds never set the field, so a sender sends them inline
batches only and does not count them as readers. An older sender never sends a chunk. No storage format change and no
format bump.

## Billing

Log shipping moves replication bytes between the peer streams and the object store only. Replication counts no billed
operation (`FreeReplicationUsageTests`). Receivers record their batches and anti-entropy pages in the replication
analytics.

## Tests and measurements

- Fast `LogChunkTests`:
  1. A chunk reference applies as its batch; an unreadable one is refused as `chunk_unread`. This holds with and
     without a bucket.
  2. A catch-up of 480 documents of 3 KiB that both `sjc` and `fra` need reaches them through chunks. Each chunk is put
     once, sealed, and read by both.
  3. A peer without a bucket (an older build) gets every batch inline, and the one remaining reader gets no chunk.
- Slow `TigrisLogChunkTests`: the same catch-up against the private CI bucket. It ran green on Tigris and on R2, with
  the configuration swapped only. It also checks that no document text appears in the stored objects.
- Benchmark: `benchmark/bench/2026-10-23-free-replication.md` (`make benchmark-repl-chunks`).
