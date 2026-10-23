---
title: Data deletion and erasure timelines
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Data deletion and erasure timelines

**Implementation reviewed:** 2026-10-23

Export any data you need before deleting it. For an erasure request, contact [privacy@terrascale.tech](mailto:privacy@terrascale.tech).

TerraScale provides the Service under Brazilian law, including the Lei Geral de Proteção de Dados (LGPD), and the GDPR where applicable. TerraScale is the controller for account data and the processor for personal data you store in your databases. See the [Privacy Policy](/pt-br/legal/privacy) and [Data Processing Agreement](/pt-br/legal/dpa).

Removing data from normal reads, removing retained history and erasing every physical copy are separate steps. A successful delete or a missing record is not proof that all copies have been erased.

## Documents, databases and organizations

| What you delete | Effect on live data | What can remain |
| --- | --- | --- |
| Document | A committed delete replaces the current document with a deletion marker (a **tombstone**). Normal reads that have applied the deletion no longer return the document. | Tombstones, changes-feed metadata, storage history and backups. Deletion must propagate before other copies reflect it. |
| Database | Deletion removes its catalog entry and attempts to remove its database files and related working files. A durable cleanup record allows unfinished file deletion to be recovered. | Registered backups awaiting cleanup, continuous backup objects and other retained copies. Catalog removal alone does not confirm their erasure. |
| Organization | An owner must delete its projects first. Organization deletion removes the organization used for authorization and attempts to clean up memberships, API keys, invitations and audit events. | Related records whose cleanup failed, storage history and backups. Organization deletion does not by itself delete a person's account. |

Document tombstones prevent older replicated writes from bringing deleted data back. Their garbage collection depends on replication progress and safety checks, rather than a fixed number of days. **There is no guaranteed upper bound for tombstone cleanup or physical storage purge in the reviewed implementation.**
Replication safety metadata may retain only opaque ULIDs and content hashes, never document content, field values, user-chosen keys, emails or names. Its required retention cap is 30 days plus the plan's PITR window (at most 30 additional days), subject to these exceptions; implementation verification remains pending:

1. A listed region that remains offline retains its replication garbage-collection hold, so purge completion waits for it. This exception also covers pending authentication deletion-job metadata containing only opaque generated subject and organization ULIDs, with no emails, names or content, retained only while a listed holder is offline and cleared by the normal job once it returns. An operator alert is required at day 20. Region removal requires explicit user authorization and is never automatic.
2. Catalog and project-closure tombstones may remain permanently, but only as opaque ULIDs with no content, field values, names, emails or user-chosen keys. This permanent exception does not include content hashes.
3. An erasure stalled by a replica epoch change stays pending and requires an operator alert that fires within 15 minutes. Operators must escalate to engineering for safe recovery; no operator re-cut command or roots-only override treats missing replication coverage as proof of erasure.

Organization cleanup can fail after the organization has been removed. The reviewed implementation does not establish a guaranteed completion deadline for those remaining records. Removal of authorization is not confirmation of complete erasure.

Source: [document deletion](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.cs#L296-L321), [tombstone garbage collection](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Gc.cs#L59-L158), [database cleanup](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Features/Management/EngineManagementService.cs#L574-L620) and [organization cleanup](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraScale.Authentication/Features/Platform/PlatformDirectory.cs#L1016-L1029).

## Changes feed

Changes-feed events retain metadata such as document identifiers and deletion markers. Events older than **7 days** become eligible for trimming. The cleanup worker runs hourly in batches and retries failures on later passes; a persistent failure can retain events longer. Seven days is therefore the retention threshold, not a guaranteed erasure deadline for the feed or for other storage.

Source: [7-day retention](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Engine/FeedPage.cs#L58-L59) and [hourly cleanup and retries](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Engine/Sqlite/FeedRetention.cs#L9-L53).

## Backups and point-in-time recovery

The current code's default point-in-time recovery (PITR) windows are listed by engine safety tier below. These tiers do not establish entitlements for commercial Free, Starter, Pro, Team or Enterprise plans; no mapping is published:

| Engine safety tier | Default PITR window |
| --- | --- |
| Hobby | 1 day |
| Pro | 7 days |
| Enterprise | 30 days |

These are recovery windows, not maximum ages for every backup copy. For your available restore points, see [Backups and restore](/pt-br/guides/backups-and-restore).

Continuous backup retention runs hourly with a **1-hour grace period**. It preserves a baseline snapshot preceding the cutoff and the history needed to restore from it. Failed deletions or storage retention rules can delay cleanup. Removing a database stops its continuous backup tracking; that is not confirmation that previously uploaded objects have been purged.

Manual and scheduled backups have a separate policy: **7 days by default**, with a supported range of **1–35 days**. Database deletion queues these registered backups for cleanup. The scheduler attempts cleanup once a minute, in bounded batches, and retains unfinished work for later passes.

Where backup-bucket snapshots are supported and enabled, the code takes them daily with a **14-day retention target**, but always preserves the newest snapshot. A snapshot can therefore outlive 14 days if no replacement succeeds. These snapshots can retain objects already removed from the current backup storage.

Customer-owned backup buckets, exports and independently created copies need separate deletion by their controller. Removing a customer backup destination keeps its existing objects, as described in the [backup guide](/pt-br/guides/backups-and-restore#your-own-backup-bucket).

Source: [plan defaults](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Configuration/BackupSettings.cs#L51-L57), [continuous retention and bucket snapshots](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Features/Backup/ContinuousBackup.Maintenance.cs#L35-L222), [removed database tracking](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Features/Backup/ContinuousBackup.cs#L533-L557) and [registered backup cleanup](https://github.com/TerraScale/terrascale/blob/49e32e99ca20c94e581bd080f61f3ca7668e715b/src/TerraBase/Features/Management/Backups/StorageSweep.cs#L19-L24).

## Existing legal commitments

The [Terms of Service, section 6](/pt-br/legal/terms#_6-data-export-and-deletion) currently state that Customer Data is deleted from active systems within **30 days** of account closure or database deletion, and from backups within the plan's PITR window plus **30 days**. The [Privacy Policy](/pt-br/legal/privacy#retention) states that account data is deleted within **30 days** of closure.

Those are the published commitments. The implementation reviewed above does not prove a bounded completion time for all organization cleanup or physical copies. This page does not replace those commitments or certify that their deadlines have been met. An erasure request needs confirmation of the affected copies and any outstanding cleanup. Retention required by applicable law remains subject to the DPA and Privacy Policy.

## Request deletion or confirmation

1. Email [privacy@terrascale.tech](mailto:privacy@terrascale.tech) with the account or organization concerned and the scope of the request: documents, databases, organization records or account closure. Include resource identifiers where available; do not send passwords, tokens or document contents.
2. If the personal data is held in another customer's database, contact that customer as controller. TerraScale assists the controller under the [DPA](/pt-br/legal/dpa#_8-deletion-and-return).
3. Request confirmation covering live data, retained metadata, backups and any remaining copies or legally required retention. A response to the request is separate from confirmation of complete erasure; the response period in the [Privacy Policy](/pt-br/legal/privacy#your-rights) is not a physical purge deadline.

</div>
