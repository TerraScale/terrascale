# Preserve current files and deletions during rollback

Original URLs and the pre-cutover byte snapshot are recovery evidence. They are
not the desired rollback state after users have created, replaced or deleted
files. Reconcile the current committed state before routing any traffic back.

## 1. Require an application change journal before cutover

Implement a durable journal in the application's persistent database, covering
every writer: user endpoints, background jobs, administrator tools and imports.
Disable direct file mutations that bypass it. The journal is application data,
not an assumed TerraScale file API feature. Use the documented
[SQL transactions](https://docs.terrascale.tech/guides/sql-http) to commit reference
changes and their journal records atomically. An object-store mutation cannot
be made atomic with that transaction: record a durable intent before the object
operation, then mark the operation committed together with the published row
references. Reconcile incomplete intents after crashes before acknowledging or
replaying them.

Before cutover, prove the approved source upload context can restore every
mapped owner's permissions throughout the rollback window, including credential
renewal. A retained service credential alone cannot establish user ownership.
Resolve that prerequisite before admitting target writes.

Each operation needs a unique operation ID, ordered sequence, commit status,
`create`/`replace`/`delete`/`reference-change` kind, logical file ID, source bucket
and rollback key, prior and new target IDs, owner mapping, MIME, size and SHA-256,
and all affected row/JSON-reference IDs with before/after values. Record a
tombstone for deletion even if physical file deletion has not finished. Maintain
per-destination replay status so an interrupted rollback can reconcile outcomes
instead of blindly repeating operations. Store no signed URLs or credentials.

- For create/replace, allocate and verify bytes first; commit the new references
  and journal event together. Keep a verified recoverable copy of every file
  still live through the rollback window. Uncommitted allocations are orphans
  to reconcile, not committed business writes to restore.
- For delete, atomically commit the tombstone, reference removals and deletion
  work item before acknowledging it. Hide tombstoned files immediately at the
  application boundary; process the physical deletion separately. Never restore
  the archived original just because the source copy still exists.
- For reference changes without byte changes, record old/new logical mappings
  and nulls, including all shared references. Do not delete a shared object while
  a committed live reference still needs it. A logical file deletion must clear
  every reference to it in the intended current state.

Example operation record for a replaced avatar; IDs and labels are nonsecret
illustrations. The original source owner comes from the approved identity map.

```json
{
  "operationId": "avatar-change-402",
  "sequence": 402,
  "status": "committed",
  "kind": "replace",
  "logicalFileId": "avatar-profile-17",
  "sourceBucket": "avatars",
  "rollbackKey": "old-user-17/photo.png",
  "priorTargetId": "01ARZ3NDEKTSV4RRFFQ69G5FAX",
  "targetId": "01ARZ3NDEKTSV4RRFFQ69G5FAY",
  "targetOwnerId": "01ARZ3NDEKTSV4RRFFQ69G5FAW",
  "references": [{"rowId": "profile-17", "field": "avatar", "before": "avatar-v1", "after": "avatar-v2"}]
}
```

Store MIME, size, checksum and recoverable-byte location alongside that record.
A subsequent deletion at sequence 403 records `targetId: null`, a tombstone and
null references. At rollback, the correct result is no source avatar and no
current row reference, even though both original and replacement bytes may have
existed earlier. A replacement with no later deletion must restore the new bytes
and point every current reference to that version.

## 2. Freeze, capture a watermark and reconcile

1. Freeze both source and target writes, including queued work and reference-only
   updates. Drain in-flight operations, resolve all durable intents, and capture
   the highest committed sequence as the rollback watermark. Keep reads on the
   current deployment or in maintenance until reconciliation passes.
2. Compare the journal with current application references and target inventory.
   Account for every committed operation through the watermark. A missing event,
   an unknown writer or an unresolved intent blocks rollback; do not guess from
   a stale original URL.
3. Fold the ordered journal and cutover map into the desired current source
   inventory and row references. Mark each operation applied or superseded by a
   later committed operation. This accounts for create→replace→delete without
   needing to recreate an object that is ultimately tombstoned. Carry tombstones
   for both the original rollback key and any superseded key when keys changed.
4. For each live file, obtain the verified current bytes and write them to its
   planned source key, replacing old bytes when necessary. Verify SHA-256, size,
   MIME, source ownership and effective policy. Creating objects with a service
   key does not establish the original user's ownership: use an approved source
   upload context that restores that owner, or stop until one is available. See
   [Supabase ownership](https://supabase.com/docs/guides/storage/security/ownership)
   and [access control](https://supabase.com/docs/guides/storage/security/access-control).
5. Clear the source references to tombstoned files, then remove every source key
   whose final state is deleted, including retained pre-cutover copies.
   Reconcile a lost delete response by checking source
   inventory before another mutation. Verify tombstoned keys are absent.
6. Apply the desired current source-format row/JSON references, including nulls
   for deletions and changed references for replacements. Use the rollback
   application's transaction model; never restore all archived column values.
   For public buckets, generate the current public URL from the reconciled key.
   For private buckets, store stable keys and mint signed URLs only when needed.
7. Invalidate application/CDN caches of replaced/deleted files and verify the
   source application as owner, unrelated user and anonymous caller. Already
   issued target capabilities retain their short lifetime as described in
   [TerraScale signed transfers](https://docs.terrascale.tech/guides/file-storage).
8. Compare current counts/bytes/checksums, every reference and every tombstone
   against the desired state through the watermark. Record replay evidence,
   switch to the retained source release, then reopen source writers. Keep the
   freeze on any failure; preserve the journal for another reconciliation pass.

## 3. Supported source operations

Use the official Supabase client in the rollback service. `sourceWriter` below
is that client's authenticated context for the original source owner, with the
source INSERT/SELECT/UPDATE permissions required for upsert. `sourceAdmin` is the
server-only client authorized to reconcile removals. Supply credentials via the
existing backend's secret/session handling, never journal fields. These snippets
illustrate the per-file operations after the desired-state plan is approved:

```js
const { error: uploadError } = await sourceWriter.storage.from(bucket).upload(key, bytes, {
  upsert: true,
  contentType,
});
if (uploadError) throw new Error('Source upload needs reconciliation');
const { data, error: downloadError } = await sourceWriter.storage.from(bucket).download(key);
if (downloadError) throw new Error('Source verification failed');
const restoredBytes = new Uint8Array(await data.arrayBuffer());
```

Compare `restoredBytes` against the desired current size and SHA-256 before
marking the operation applied. On a lost upload response, download/compare the
source first. For a final tombstone, after clearing all references:

```js
const { error } = await sourceAdmin.storage.from(bucket).remove([key]);
if (error) throw new Error('Source deletion needs reconciliation');
```

Verify absence through the source SQL inventory and source download API. These
methods and permissions are documented under
[upload](https://supabase.com/docs/reference/javascript/storage-from-upload),
[download](https://supabase.com/docs/reference/javascript/storage-from-download)
and [remove](https://supabase.com/docs/reference/javascript/storage-from-remove).
Apply the retention policy to archived deleted bytes too; a backup must never be
used to republish data contrary to its committed deletion tombstone.
