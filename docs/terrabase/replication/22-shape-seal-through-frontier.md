# Shape seals through the remote durable frontier (RT2 lane K)

Amends the chunk seal of [shape-reads.md section 10](../shape-reads.md#10-durability-and-publication) and
[free-reads.md 3.4](../free-reads.md). The publication gate `F ≥ max(covered_offset, values_at)` is unchanged.

**Problem.** A chunk sealed only when `F` equalled the committed head. A steady writer whose commits arrive faster than one
remote acknowledgement keeps the head ahead of `F`, so seals succeeded only in write gaps. On the RT2 cells (20 documents/s
into sjc, nearest ack 130 ms) live readers received a write after 17.7 s p50 / 32 s p99 and first answered
`503 unavailable` at the 20 s proof bound.

**Rule.** Values are still read at the head (no copy-on-overwrite, D12). In a cell, a chunk covers commits through `F` whose
documents have no feed row above `F`, and is stamped `values_at = F`. Every emitted value then equals its value at `F`, so
the chunk passes the unchanged gate. It ends before the first commit with a document written above `F`; when that is the
first commit, nothing is sealed and the seal waits for `F`, as before.

- A collection with unique constraints seals only while none of its documents has a feed row above `F`: a uniqueness
  write can hide or show another document without that document's own feed row.
- More than 4096 feed rows above `F` (a stalled or far-behind frontier) seals nothing, as before, rather than load them.
- A cell of one is unchanged: `F` is the head.

**Limit.** A document rewritten faster than one remote ack still holds back the commits from its first unsealed write.

`F` needs one other placed region's ack, not every region's: each link's ack proves its range
(`PeerStreams.BookkeepAsync`).
