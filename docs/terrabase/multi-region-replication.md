# Multi-region replication on Turso (design C)

- Status: design, 2026-10-11; slices land as listed in section 21. Each guarantee holds only once its slice ships
  with integration and e2e evidence.
- Decision: design C (user, 2026-10-11). Turso is the storage engine, Turso CDC is the change capture, and TerraBase's
  own Anna-style lattices and gRPC streams do the replication, over the private service network. Turso's sync engine is not used: it
  resolves by last push and its self-hosted hub is test-only.
- Inputs: the replication design, phase 3a (PR #11), the e2e suite (PR #6), and
  [Anna core](design/anna-core.md). Where they conflict, this document replaces anna-core §3 to §9 and decisions
  [0009](decisions/0009-catalog-replication.md) and [0010](decisions/0010-write-wait-and-synced-durable-ack.md).

The design is split into one file per topic under [`replication/`](replication/). Add a new file for a new
topic instead of editing an existing one; this index changes only when a file is added. Section numbers cited
elsewhere ("section 15") are the file number prefixes.

- [1. Summary](replication/01-summary.md)
- [2. Turso 0.8.2 constraints this design obeys](replication/02-turso-0-8-2-constraints-this-design-obeys.md)
- [3. Identity, HLC and stamps](replication/03-identity-hlc-and-stamps.md)
- [4. Document lattice](replication/04-document-lattice.md)
- [5. Storage layout (one Turso file per database)](replication/05-storage-layout-one-turso-file-per-database.md)
- [6. Write path](replication/06-write-path.md)
- [7. CDC tailer → change groups](replication/07-cdc-tailer-change-groups.md)
- [8. Transport](replication/08-transport.md)
- [9. Receive and merge path](replication/09-receive-and-merge-path.md)
- [10. Anti-entropy and refill](replication/10-anti-entropy-and-refill.md)
- [11. Garbage collection](replication/11-garbage-collection.md)
- [12. SQL writes](replication/12-sql-writes.md)
- [13. Counters and sets in the document API](replication/13-counters-and-sets-in-the-document-api.md)
- [14. Changes feed](replication/14-changes-feed.md)
- [15. Catalog (`_system`) as a lattice database](replication/15-catalog-system-as-a-lattice-database.md)
- [16. Home region and live move](replication/16-home-region-and-live-move.md)
- [17. ts-auth on lattices](replication/17-ts-auth-on-lattices.md)
- [18. Failure modes](replication/18-failure-modes.md)
- [19. Deleted code](replication/19-deleted-code.md)
- [20. Definition of done (e2e)](replication/20-definition-of-done-e2e.md)
- [21. Slice plan](replication/21-slice-plan.md)
- [21. Slice plan: R2a as built](replication/21-r2a-as-built.md)
- [21. Slice plan: R2b as built](replication/21-r2b-as-built.md)
- [21. Slice plan: R2c as built](replication/21-r2c-as-built.md)
- [21. Slice plan: R4a as built](replication/21-r4a-as-built.md)
- [21. Slice plan: R6 as built](replication/21-r6-as-built.md)
- [21. Slice plan: R7 as built](replication/21-r7-as-built.md)
- [21. Slice plan: Contract for R3](replication/21-contract-for-r3.md)
- [21. Slice plan: T1 as built](replication/21-t1-as-built.md)
- [21. Slice plan: R3 as built](replication/21-r3-as-built.md)
- [21. Slice plan: R8 as built](replication/21-r8-as-built.md)
- [21. Slice plan: R10a as built](replication/21-r10a-as-built.md)
- [21. Slice plan: Order and parallelism](replication/21-order-and-parallelism.md)
- [21. Slice plan: Replication outage window](replication/21-replication-outage-window.md)
- [21. Slice plan: Sender ACK bookkeeping as built](replication/21-sender-ack-bookkeeping.md)
- [22. Slice plan: Receiver database isolation as built](replication/22-receiver-database-isolation.md)
- [22. Shape seals through the remote durable frontier](replication/22-shape-seal-through-frontier.md)
- [22. Shape snapshots reseal at 16 chunks per page](replication/22-shape-snapshot-reseal-per-page.md)
- [22. Open questions and risks](replication/22-open-questions-and-risks.md)
- [23. Peer stream protocol version and pipelining](replication/23-protocol-version-and-pipelining.md)
- [24. Log chunks: change groups through the object store](replication/24-log-chunks.md)
- [25. Row-derived repair digests](replication/25-row-derived-repair-digests.md)
- [26. Replication through the HTTP and socket endpoints](replication/26-endpoint-matrix.md)
- [Receiver apply progress and the batch inactivity deadline](replication/receiver-apply-progress.md)
- [Live replay of admitted writes after a cut-log refill](replication/admitted-write-refill-replay.md)

- [TS-1012: catalog admission after collected revocations](replication/ts1012-collected-revocations.md)
