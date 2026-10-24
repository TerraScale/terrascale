# Webhook logical erasure and causal purge receipts (TS-873)

This adds logical erasure of a permanently deleted database's webhook state and binds purge receipts to the authority they verified. It does **not** prove physical removal of earlier SYSTEM history (CDC, WAL, free pages, sealed shapes, replicas or backups); that remains the shared-history work in [deletion-retention.md](deletion-retention.md). The Terms are unchanged.

## What a node certifies

Before a node writes its `DatabasePurgeReceipt`, `IWebhookErasure.EraseAsync` must succeed for the exact (organization, project, database) scope, and `IWebhookErasure.Certifies` must still hold inside the receipt's own write transaction. Together they mean, at that point on that node:

- no registration, cursor, delivery body or delivery history remains in the database's `databases/<id>/webhook*` prefix;
- no global `webhook_dispatch` row of that scope remains, whichever node owned it, including an owner that is offline for good;
- no webhook send for the database is still in flight; sends are cancelled and drained, never disposed under their owner.

Each call does bounded work (four dispatch pages) and resumes its pass; only a complete pass certifies. A restart starts the pass again.

A receipt is point-in-time absence of that exact scope only. It is not a promise that no dispatch naming the database id can ever exist afterwards.

## Admission of replicated state

SYSTEM receives remote state only through `StorageChangeGroupSink`: stream groups and anti-entropy repairs, which is also how a SYSTEM stream refill arrives. In-process rebuild refuses SYSTEM. Inside each receiving transaction, webhook state joins after every other change, so a purge record arriving in the same group counts. Under a readable format-1 purge record:

- every row of the database's prefix is refused without reading its body; the prefix is the database's own namespace;
- a dispatch of the purged scope, or one claiming the database id without a readable organization or project, is refused.

Independently of any authority, a dispatch whose body holds the reserved scalar-wrapper field (`"\u0000"`) is refused: local writes refuse that field, and when read back it would hide the row's tenant. Such a row stored before this check blocks certification for any database whose id it contains.

Refused state is never joined, so neither its row nor its change history is written. It still counts as applied, so the origin's stream moves on. Payload-free dispatch deletions (tombstones) join normally; prefix tombstones under authority are refused with the rest of the prefix.

Admitted, never discarded:

- state of other databases;
- state whose purge record is unreadable (a scalar, a repeated key) or of another format; webhook admission and erasure treat that as no authority, while the database deletion fence itself still sees any row under the id;
- a dispatch naming the database id under a different (organization, project) pair, both readable; the pair differs when either identifier does. Such a conflict is counted in `terrabase.webhooks.replica_conflicts`. Erasure then refuses to certify on this node, and a conflict committed before the receipt transaction withholds the receipt.

A conflict that arrives after a receipt belongs to another scope, so the receipt never covered it. It is kept, counted by the same metric, and makes later `EraseAsync`/`Certifies` calls return false. The receipt itself is not revoked.

Tenant ids are read without throwing: names match case-insensitively like the typed reader, undecodable escapes name nothing, and nesting up to what a decoded state can materialize (the stored limit per field, plus a set element and the root object) is accepted. Typed reads of purge records and dispatches treat a repeated key or a scalar as unreadable. A malformed row therefore neither stalls replication nor fails a worker pass for its neighbors. The backup purge listing moves past unreadable purge records without reporting them complete; convergence keeps counting them as pending purge errors.

## Causal receipts

A receipt records `Authority`, which is the `RequestedAt` of the purge record the issuing node read in the same transaction that verified absence. Both completion checks (this node's own receipt during convergence, and every expected holder's receipt in `AreActiveCopiesAbsentAsync`) require `Authority == RequestedAt` plus the same holder and full tenant. `VerifiedAt` is the holder's own clock; it is informational and never compared with another node's clock. Backup verification likewise no longer compares the verifier's clock with the request time.

## Rollout

Receipts written before this change carry no `Authority` and no longer count:

- a live holder re-verifies on its next convergence pass and reissues a bound receipt;
- a removed or permanently unreachable holder cannot reissue, so its purge stays pending indefinitely (visible through the existing pending and overdue purge metrics). Clearing it needs that holder's recovery or independently verified storage destruction, as for any unreachable holder.

No migration rewrites old receipts.
