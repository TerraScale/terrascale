# Reconcile the final frozen source snapshot

Use this procedure before target traffic starts. Freeze all source writers and
drain jobs first. Keep target migration buckets exclusively owned by this run;
disable preview readers while retiring IDs. A later rollback uses the separate
[post-cutover journal](rollback.md), not this pre-cutover delta procedure.

TerraScale files are immutable and deletion prevents new downloads while queuing
physical cleanup. The documented `list`, `deleteFile`, upload and completion
methods are in [file storage](https://docs.terrascale.tech/guides/file-storage).
The following scripts use the current SDK installation from the
[JS quickstart](https://docs.terrascale.tech/getting-started/javascript).

## 1. Archive the previous generation

From the migration directory, with the copier stopped and all previous entries
verified, archive the original manifest, checkpoint and source bytes. The archive
contains private customer data: keep it protected under the approved retention
policy and do not commit it. Use a new directory name for each generation.

```bash
umask 077
mkdir before-final
cp manifest.json before-final/manifest.json
cp state.json before-final/state.json
cp -a staging before-final/staging
cp "$SKILL_DIR/scripts/retire-delta.mjs" retire-delta.mjs
mkdir staging-final
```

Keep the prior DB-reference map alongside this archive. `state.json` continues
to be the live checkpoint; `before-final/state.json` preserves superseded IDs.
Reconcile pending uploads using [interruption recovery](recovery-and-urls.md)
before continuing. Never reset the whole checkpoint to bypass a checksum error.

## 2. Inventory and stage the final generation

Repeat the source SQL inventory in the parent skill under the write freeze.
Build `manifest.final.json` with every current source object, updated sizes/MIME,
the approved owner ULIDs and target bucket IDs. Exclude deleted source keys.
New access boundaries need their matching target buckets created first.

For every current source bucket, including newly empty buckets, run:

```bash
mkdir -p "staging-final/$SOURCE_BUCKET"
rclone copy "source:$SOURCE_BUCKET" "staging-final/$SOURCE_BUCKET"
rclone check "source:$SOURCE_BUCKET" "staging-final/$SOURCE_BUCKET" --download
rclone size "source:$SOURCE_BUCKET" --json
```

Use the parent's S3 environment configuration. These commands follow
[rclone copy](https://rclone.org/commands/rclone_copy/),
[rclone check](https://rclone.org/commands/rclone_check/) and
[Supabase S3 compatibility](https://supabase.com/docs/guides/storage/s3/compatibility).
The fresh staging directory avoids retaining locally deleted source objects.
Reconcile per-bucket SQL counts/bytes, manifest counts/bytes and rclone output.

```bash
node copy.mjs manifest.final.json staging-final state.json --dry-run
node retire-delta.mjs before-final/manifest.json manifest.final.json staging-final state.json --dry-run
```

The retirement dry run compares exact bucket/key pairs and frozen local checksums
against the prior checkpoint. It also treats bucket, owner or MIME changes as
replacements even if bytes are unchanged. It reports replacements, deletions,
additions and unchanged objects without network requests or file writes.
For example, one changed avatar, one deleted attachment, one new object and one
unchanged object produce `Delta: 1 replacements, 1 deletions, 1 additions, 1 unchanged`.

## 3. Retire obsolete IDs, then copy replacements

Review the counts against the inventory before running this explicitly
destructive step. All source bytes and prior mappings must already be archived.

```bash
node retire-delta.mjs before-final/manifest.json manifest.final.json staging-final state.json --apply
node copy.mjs manifest.final.json staging-final state.json --apply
```

Retirement first verifies the entire previous target inventory, including
checksums, owners and MIME metadata, and refuses any uncertain checkpoint or
unexpected target file. It writes `state.json.delta.json` with the exact obsolete
IDs and the proposed pruned checkpoint before deleting anything. It deletes only
replaced/deleted IDs, pages through lists to confirm their absence, and then
atomically replaces `state.json` with the pruned version. Unchanged entries keep
their verified IDs. The archived checkpoint retains every obsolete ID.

The copier allocates new immutable IDs for replacement and new keys, verifies
their downloaded bytes and metadata, and compares the exact current inventory.
Rebuild the DB reference map from `manifest.final.json` and this final checkpoint:
use new IDs for replacements, keep IDs for unchanged keys, and remove references
to deleted keys or set them null according to application semantics. Do not read
the archived mapping as the current mapping. Verify every row and JSON reference
using the parent skill's [parameterized procedure](recovery-and-urls.md).
Verify every empty bucket separately, including buckets whose last key was
deleted. Only after counts, bytes, ownership, references and obsolete-ID absence
all pass may the application switch to the target.

## Interrupted retirement

If retirement fails, stop the copier. A subsequent apply refuses an existing
delta journal; do not erase it to force a replay. Read `retired` in the journal
and, for each listed bucket, page through all readable completed files using the
listing loop in [recovery](recovery-and-urls.md). For each planned ID still
present, verify its checksum against the archived checkpoint, explicitly call
`await admin.storage.bucket(bucketId).deleteFile(id)`, and list again. For an ID
already absent, record its retirement without issuing another deletion. Stop on
any unexpected file, checksum mismatch or unavailable service.

After every planned obsolete ID is confirmed absent and all unchanged IDs still
match the archive, commit the journal's proposed checkpoint from the protected
migration directory with this local command:

```bash
node --input-type=module <<'JS'
import { readFile, writeFile, rename } from 'node:fs/promises';
const journal = JSON.parse(await readFile('state.json.delta.json', 'utf8'));
await writeFile('state.json.tmp', JSON.stringify(journal.nextState, null, 2), { mode: 0o600 });
await rename('state.json.tmp', 'state.json');
JS
```

Then rerun the final copier. If it was interrupted too, use its existing pending
upload reconciliation, preserving newly verified replacement IDs. Archive the
delta journal after final verification; never re-run retirement against an old
manifest once new files have been allocated. If the source changes again, freeze
it and archive a new full generation before planning another delta.
