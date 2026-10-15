# 22. Slice plan: Receiver database isolation as built

**What changed.** The receiver of `Replication.Push` (`PeerStreamReceiver`) no longer joins a stream's batches one at a
time across every database. Each database's batch or frontier runs beside the link loop, like a refill already did. The
link loop still reads every frame, answers heartbeats at once and is the only writer of answers.

**Why.** The loop awaited `ApplyAsync` and `RecordFrontierAsync` for each frame in turn. A busy database's writer queue
(5 to 8 s per wait on a loaded machine) then delayed every other database's ACK on the same link past the sender's 10 s
confirmation bound, and a 15 s silence ended the link, which reconnected and resent. This is the receiver half of the
problem `21-sender-ack-bookkeeping.md` fixed on the sender.

**Order.** Within a database, frames still apply and are answered in stream order: a database's next batch starts after
its frame in progress, and answers of one database are written in arrival order. Answers of different databases can
come in any order; the sender matches ACKs by database. An ACK is still written only after the batch's durable commit.
A refill of a database starts once that database's frame in progress ended, and its answer follows that frame's. Refill
refusal, the cut kept across streams, incarnation checks and growing change ids are unchanged.

**Catalog first.** The sender lists the system database first, and the old loop committed a catalog batch before it
started the next client batch. A client database's batch or refill therefore also waits for the system database's
frame in progress: a database the catalog batch registers is otherwise refused as unregistered (the sink applies only
registered databases), and the sender retries it 5 s later. Client databases still never wait for each other.

**Bounds.** Per stream, at most 64 databases have a frame in progress, with at most one batch each; a database's frame
may wait behind one frontier. A frontier is an optional proof the sender repeats every 5 s: it is dropped, unanswered,
behind its database's work or once 32 databases are in progress. A second batch of a database in progress, or a batch
past 64, is refused; a sender that awaits each batch's ACK (at most 32 outstanding) never reaches either. Each batch
reserves its wire bytes plus three times the decoded size its envelope declares (at most 4 MiB) from the replication
memory lane before it is decoded, and holds them until it applied, not until its ACK is written; a batch without them is
refused and sent again. Refill
admission and its reservation are unchanged.

**Streams.** A replaced or ending stream cancels its frames in progress and joins them, with its refills, before the
origin's next stream starts.

**What it does not fix.** One database whose writer waits longer than 15 s still makes the sender end the whole link
(its per-batch progress deadline). Under replication memory pressure a batch is refused instead of queued, so the sender
retries it after 5 s: a refusal, not a delayed ACK. A client batch behind a slow catalog commit waits for it, as before.
That this removes the ACK delays seen on loaded machines is not shown on a live cluster; the Fast test holds one
database's writer on a real server. Whether CI's single end-to-end database is held back by its own writer pressure,
rather than by other databases on the link, is not established by this change.
