# Recover uploads and rewrite file references

These procedures use the API and SDK operations in
[TerraScale file storage](https://docs.terrascale.tech/guides/file-storage),
the client setup in the
[JS quickstart](https://docs.terrascale.tech/getting-started/javascript) and
parameterized statements in [SQL over HTTP](https://docs.terrascale.tech/guides/sql-http).
Run from the protected migration directory, with its installed current SDK.

## Interrupted copy

Stop the copier before editing its state. Keep a backup of `state.json`. Entries
in `files` are keyed by JSON-encoded `[sourceBucket, sourceName]` pairs.

| Checkpoint | Action before rerunning |
| --- | --- |
| `verified` | Rerun with the same frozen manifest. The copier rechecks downloaded bytes and final inventory. |
| `pending`, with an ID | List all completed files in that target bucket. If the ID exists, check size, SHA-256, MIME, `metadata.userId` and `metadata.migrationKey` against the manifest and source bytes. Download it and compare SHA-256. Only then change that entry to `verified`. |
| `pending`, ID absent from completed list | The signed PUT or completion may have failed. Attempt `bucket.completeUpload(id)` once as explicit reconciliation. If successful, download/verify before marking verified. If incomplete or expired, delete that reservation with `bucket.deleteFile(id)`, retain its ID in the operator journal, then remove only its checkpoint entry and rerun. Stop on ambiguous errors. |
| `allocating`, without an ID | A reservation response may have been lost. Search completed files by `metadata.migrationKey`, checking bytes and metadata before adopting exactly one ID. If absent, allow the five-minute capability to expire and cleanup to release its reservation before clearing this entry. Keep source writers frozen and investigate usage; do not repeatedly allocate on a full allowance. |
| Duplicate matching IDs | Stop; compare bytes/metadata and select one canonical ID. Delete other copies deliberately and reconcile the state and reference map. |

No signed URL is saved in the checkpoint. Do not capture raw errors containing
capabilities or authorization headers. A 401 needs credential/expiry diagnosis;
403 needs scope/rule diagnosis; 429 needs allowance diagnosis; 503 needs service
availability diagnosis. See the status table and cleanup semantics in
[file storage](https://docs.terrascale.tech/guides/file-storage). Resume after the
cause is fixed, without replaying an uncertain allocation.

For an empty source bucket, verify it separately with the same paginated listing:

```js
const files = [];
let after;
do {
  const page = await admin.storage.bucket(bucketId).list(after ? { after } : {});
  files.push(...page.items);
  after = page.next;
} while (after);
if (files.length !== 0) throw new Error('Expected an empty bucket');
```

## Rewrite rows using stable IDs

Example application schema: `profiles(id, avatar_url)` stores the original public
URL. After migrating the application's database, add `avatar_bucket_id` and
`avatar_file_id`. Keep the initial `avatar_url` as recovery evidence. Deploy a
reader that prefers the IDs only when the migration switch is enabled; journal
every later reference change as described in [rollback reconciliation](rollback.md).

The example below is a filled single-row mapping. Set `oldUrl` from the protected
inventory; parse Supabase URLs using URL path components and the known bucket
prefix, decoding the object name exactly once. Signed URLs are transient: map
their bucket/key, remove secret query parameters from the journal, and regenerate
them on demand. See
[Supabase signed URLs](https://supabase.com/docs/reference/javascript/storage-from-createsignedurl)
and [public URLs](https://supabase.com/docs/reference/javascript/storage-from-getpublicurl).

```js
const db = admin.database(process.env.TERRASCALE_DATABASE_NAME);
await db.execute('ALTER TABLE profiles ADD COLUMN avatar_bucket_id TEXT');
await db.execute('ALTER TABLE profiles ADD COLUMN avatar_file_id TEXT');
const sourceKey = JSON.stringify(['avatars', 'old-user-17/photo.png']);
const copied = state.files[sourceKey];
if (!copied || copied.phase !== 'verified') throw new Error('Unverified file');
await db.execute(
  'UPDATE profiles SET avatar_bucket_id = ?, avatar_file_id = ? WHERE id = ? AND avatar_url = ?',
  [copied.bucketId, copied.id, 'profile-17', oldUrl],
);
const rows = await db.query(
  'SELECT avatar_bucket_id, avatar_file_id FROM profiles WHERE id = ?', ['profile-17'],
);
if (rows.length !== 1 || rows[0].avatar_file_id !== copied.id) throw new Error('Row mapping failed');
```

Run schema additions once, after checking existing columns. For all rows, build
an explicit mapping including row ID, old URL, source bucket/key and target IDs;
apply parameterized updates in bounded batches. TerraScale HTTP batches are
atomic, up to 256 statements and 1024 changed rows, with other payload/time
bounds in [SQL over HTTP](https://docs.terrascale.tech/guides/sql-http) and
[limits](https://docs.terrascale.tech/limits). Check each row, including JSON
references and references shared by several rows. Never persist a target signed
URL: `bucket.downloadUrl(id)` returns a capability valid for 60 seconds.

Before rollback disables the ID reader, replay the complete post-cutover change
journal under a write freeze using [rollback reconciliation](rollback.md). Set
the source-format references to the resulting current files, including nulls and
tombstones for deletions. Retained original URLs alone are insufficient: they
may name deleted files or superseded versions. Keep both mapping generations
and avoid destructive schema cleanup until the rollback window closes.
