# Deletion retention and verification (TS-873)

This implementation adds durable component evidence. It does **not** establish complete account-closure erasure. TS-873 remains open for the cross-service handoff and shared-database physical-history work described below. The Terms are unchanged.

## Database deletion

An explicit customer database deletion atomically records permanent SYSTEM authority, including the original request time, the 30-day active deadline, the backup deadline, and expected copy holders. The backup deadline captures the actual configured plan PITR days plus 30 days. Retry never moves either deadline. For example, configured nine-day retention produces a 39-day backup deadline. Production GRU configuration observed on 2026-10-23 used seven days for every plan; that observation establishes 37 days for that configuration only.

Replica removal, name loss and abandoned creation do not create customer deletion authority. Permanent deleted-ID records survive ordinary catalog tombstone collection and fence routing, adoption, placement, creation, restore and rebuild publication. A new database may reuse the display name with a different ULID. Replication tombstone horizons and persisted GC floors are unchanged.

Every node records its own receipt only after engine verification finds no scoped files, open pooled sessions, backup readers, salvage copies or salvage readers. A native-close failure prevents verification. Logical HTTP deletion can return 204 while readers finish; its durable cleanup intent remains reserved and convergence retries. Authority arriving during cleanup cannot generate a receipt unless that cleanup actually verified erasure.

Expected holders conservatively include retained catalog members and the local node. Unresolved placement regions remain pending. Unknown historical disks absent from membership cannot be discovered by this inventory. An unreachable holder needs recovery and purge, or independently verified storage destruction; removing membership is insufficient. No automatic legal-retention exception is invented for a failed deletion.

## Backup deletion

Destination revisions are archived before detachment or database state removal. Cleanup credentials remain sealed and are used only by the backup owner. The worker waits for all expected active-copy receipts before deleting the exact database prefix, preventing a known live backup reader from uploading afterward.

Each destination has a durable version and multipart-upload cursor. Protected entries and timed-out mutation pages do not prevent later pages or destinations from being attempted. Explicit versions, delete markers, multipart uploads and current objects are checked. Provider acknowledgements alone do not complete the job: complete absence inventories and bucket snapshot checks must succeed. Customer-owned bucket snapshots remain pending until their owner expires them; the server does not delete unrelated bucket contents.

Live backup expiry removes hard-expired snapshots and WAL, including the last baseline. Bucket expiry also removes the last expired bucket snapshot if new snapshot creation fails. This can shorten the available restore window when fresh snapshots fail; retaining an old baseline indefinitely would violate expiry. Unavailable stores, denied mutations and unresolved inventory leave evidence pending. Historic destinations detached before revision archiving, unavailable credentials, unknown platform buckets and providers without usable version/snapshot inventory still need reconciliation.

The SYSTEM authority exposes pending, overdue, oldest-request age and bounded failure counters. Backup retries expose failure counters. Monitoring and successful retries do not guarantee deletion during an indefinite outage.

## Auth cleanup

Account, organization and project roots create logical deletion jobs in the same SDK transaction that removes the authorized root. The stable job ID is a permanent write fence for related records. Startup and one-minute retry passes process bounded, fair batches; conditional writes preserve unrelated tenants and newly reused email identities. Cleanup includes all invitation states and owned invitation/token reference claims.

`AuthLogicalVerifiedAt` means that the job's auth logical records were verified absent. It is not a physical or cross-service erasure certificate. Residual owner memberships and surviving organization projects remain pending for their owning workflow; they are not blindly deleted or certified.

## Remaining closure work

1. Auth project and account closure use a durable intent and TerraBase's permanent empty-project fence, accepted only once every catalog holder confirmed it ([project-closure.md](project-closure.md), [../auth/project-closure.md](../auth/project-closure.md)). Open: a machine joining membership after the acceptance read, unreachable holders (pending forever), and a refusal that meets an intent whose fence answer was lost or raced (the project stays, with an empty permanently fenced TerraBase project).
2. Auth and SYSTEM use shared databases. Logical deletion does not prove removal from their CDC, sealed shape snapshots, free pages, WAL or historical replicas. A verified engine rewrite/history-erasure path is still required for those scopes.
3. Complete historical copy/destination inventory and evidence for unreachable storage require operational reconciliation. Pending jobs must remain open when evidence is missing. No whole-account completion endpoint or legal exception certificate is introduced here.

## Measured verification

Fast integration coverage uses real hosts/SDK calls and controlled clocks for fixed deadlines, restart/failure recovery, held readers, late authority, collected catalog tombstones, ID rejection, unrelated-file preservation, retention refusals, multipart/version continuation and independent destinations. The PR records the exact commands and results. CI remains the integrated validation gate; focused local results do not establish live physical erasure.
