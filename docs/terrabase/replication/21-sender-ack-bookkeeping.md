# 21. Slice plan: Sender ACK bookkeeping as built

**What changed.** The sender's stream consumer (`PeerStreams`) no longer takes a database's writer. A validated ACK
(in flight, pinned incarnation, not refused) advances the in-memory cursor at once. A live ACK from a placed
other-region peer then releases client waits once `HeadAsync` reads that the copy in place is still the ACK's
incarnation. That read takes no writer. The writer work moves to one bookkeeper task per connection: `ProveAsync`,
`AckAsync` and `RecordStableAsync`.

**Why.** Each ACK took the writer twice before its waits were released. On a busy machine each wait took 5 to 8 s. That
passed the 10 s confirmation bound, so writes answered `write_outcome_unknown`. The stream also stopped shipping
batches meanwhile, for every database on the link.

**Ledger.** Pending bookkeeping is coalesced per database copy (database, incarnation). It keeps the live ranges to
prove (merged, at most 16), the newest ACK and the highest stable stamp. It holds at most 1,024 pending copies plus one equally bounded batch being persisted.
When either copy or disjoint-range capacity is full, the consumer waits for the bookkeeper; proof ranges are never dropped by the ledger. For each copy, the bookkeeper proves its ranges before it
records the ACK (FR-25). A proof failure is logged, and the next ACK within the same live run covers the same range.

**What is unchanged.** Eligibility (decision 0013a, B2) is unchanged. So is the order of proof before ACK. A refused or
other-incarnation ACK confirms nothing. Bookkeeping lost with a stream is redone after the reconnect, because the
stream resends from the persisted ACK. A refused persistence no longer resets the cursor directly. The next pass's
source read sees the replaced copy and starts over.

**Consequence.** `remote_durable` can be answered before the sender has persisted the ACK or advanced the shape
frontier. Shape reads make no read-your-writes promise (shape-reads.md section 15), and seals wait for the frontier.

A replacement after the read-only incarnation check does not retract a valid confirmation that the peer durably held the original copy. The later conditional bookkeeping write refuses the replaced copy and logs that refusal; it never advances the replacement's persisted cursor. The next source read resets the stream cursor to the current copy.
