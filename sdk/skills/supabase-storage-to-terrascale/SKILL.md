---
name: supabase-storage-to-terrascale
description: Migrate Supabase Storage buckets, objects, policies and file URLs to TerraScale. Use when moving files off Supabase, replacing Supabase signed URLs or migrating storage.objects ownership and access rules.
---

# Migrate Supabase Storage to TerraScale

Use this skill for file storage, including application references to files. Database
and identity migrations are separate prerequisites; agree the identity mapping first.
Keep source objects and the old application release until rollback expires.

## Prerequisites and release gate

- [ ] Confirm the target project's file API is available. The public
  [file storage guide](https://docs.terrascale.tech/guides/file-storage) says rollout
  is in progress and live upload/download/authorization verification is pending.
  If the API is unavailable, stop before copying or changing application traffic.
- [ ] Use a server-held, unrestricted project credential with read/write access;
  database-restricted credentials cannot manage files. See
  [file storage](https://docs.terrascale.tech/guides/file-storage) and
  [authentication](https://docs.terrascale.tech/guides/authentication).
- [ ] Prepare Node.js 22 or 24, Bash, jq, rclone, psql and a protected working
  directory. Turn off shell tracing. Supply secrets through environment variables
  or a secret manager; do not paste them into commands, output or the migration log.
- [ ] Prepare Supabase S3 credentials and the exact endpoint/region from project
  Storage settings. S3 access keys bypass Storage RLS; keep them on the migration
  server. Follow [S3 authentication](https://supabase.com/docs/guides/storage/s3/authentication).
- [ ] Install the current TerraScale JS SDK from a checked-out repository, as the
  [JS quickstart](https://docs.terrascale.tech/getting-started/javascript) directs.
  The [SDK guide](https://docs.terrascale.tech/guides/sdks) identifies npm 0.0.17 as
  the previous API. Do not assume an arbitrary npm version has these file methods.

## 1. Inventory buckets, policies and consumers

- [ ] In the source SQL editor, capture these read-only queries to restricted files.
  Bucket size/MIME restrictions and public access are described in
  [creating buckets](https://supabase.com/docs/guides/storage/buckets/creating-buckets);
  RLS, `owner_id` and service-key bypass are described in
  [Storage access control](https://supabase.com/docs/guides/storage/security/access-control).

```sql
SELECT id, name, public, file_size_limit, allowed_mime_types
FROM storage.buckets ORDER BY id;

SELECT policyname, permissive, roles, cmd, qual, with_check
FROM pg_policies WHERE schemaname = 'storage' AND tablename = 'objects'
ORDER BY policyname;

SELECT bucket_id, count(*) AS objects,
       sum((metadata->>'size')::bigint) AS bytes,
       count(*) FILTER (WHERE owner_id IS NULL) AS ownerless
FROM storage.objects GROUP BY bucket_id ORDER BY bucket_id;

SELECT bucket_id, name, owner_id, metadata
FROM storage.objects ORDER BY bucket_id, name;
```

- [ ] Record every SELECT/INSERT/UPDATE/DELETE rule, restrictive policy, role,
  folder predicate, JWT claim and database lookup used by a policy. A service-key
  copy succeeding proves nothing about end-user authorization.
- [ ] Search application code and configuration for consumers:

```bash
rg -n 'supabase\.storage|storage/v1|createSignedUrl|getPublicUrl|download\(|upload\(|upsert' .
```

- [ ] Find URL columns, JSON values, email templates, caches, image transforms,
  CDN rules, mobile clients and external integrations. Inventory signed URLs by
  their source bucket/key, without retaining their secret query strings.
- [ ] Include empty buckets, deleted-object expectations and in-flight uploads.
  Freeze source writes for the final snapshot; metadata and object bytes must
  describe the same version. Never write directly to `storage.objects`; see
  [Storage schema](https://supabase.com/docs/guides/storage/schema/design).

## 2. Plan ownership, grants and incompatible behavior

The target rules below come from
[TerraScale file storage](https://docs.terrascale.tech/guides/file-storage).
Translate the effective policy, including the intersection of restrictive rules.
Do not translate each Supabase predicate into a permissive grant independently.

| Supabase behavior | Target plan |
| --- | --- |
| Owner matches `auth.uid()` or `owner_id` | Set fixed bucket owner pointer `/userId`; set each file's `metadata.userId` to the mapped ts-auth subject; enable the owner grants needed. |
| Authenticated users can read | `read.public: true` allows authenticated project end users. |
| Anonymous public URL | No equivalent anonymous grant: use an application endpoint/CDN that explicitly publishes approved content, or keep this bucket on Supabase until that endpoint exists. |
| Trusted editor role | Use `roles: ['editor']` with a trusted signed ts-auth role, after checking case and membership. |
| Folder, tenant lookup, claim combinations, different UPDATE/DELETE rules | No equivalent arbitrary RLS predicates or separate per-operation grants are documented. Split buckets along access boundaries or enforce the original decision in a server gateway. Deny direct end-user access that would bypass it. |
| Bucket MIME/size restrictions | No configurable bucket restrictions are documented. Enforce the source allowlist and size cap in the gateway before authorizing uploads; use a gateway-only write policy if direct writes could bypass it. |
| Overwrite/upsert or metadata change | Files and metadata are immutable: upload a replacement and atomically change the application's file reference, then delete the old file after rollback expires. |

- [ ] Resolve every Supabase owner to its target subject. Explicitly assign an
  approved service owner for ownerless objects; never guess from a path. Every
  mapped owner must be a canonical ULID, including service owners, as required
  by [end-user token minting](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users).
  The copier rejects invalid target owners before network requests or state writes.
- [ ] Reject zero-byte objects and objects above 104857600 bytes (100 MiB);
  metadata must fit 16 KiB. Keep incompatible objects on the source or redesign
  the application representation and verify reconstruction before cutover.
- [ ] Plan capacity for source copies and target reservations. File/database bytes
  share the organization allowance, and pending uploads reserve full size; see
  [storage cleanup](https://docs.terrascale.tech/guides/file-storage) and
  [limits](https://docs.terrascale.tech/limits).
- [ ] Obtain ts-auth end-user data access tokens through the backend exchange in
  [end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication).
  A Supabase JWT or ID token is not a replacement for that token.

Filled plan example: source private `avatars`, owner-only read/write, `image/png`
only, maximum 1 MiB; map source owner `old-user-17` to target subject
`01ARZ3NDEKTSV4RRFFQ69G5FAW`. Use this same mapped ULID in backend token minting
and file metadata; a display name or Supabase UUID cannot substitute for it.
The application gateway enforces the MIME/size rule and uploads on the user's
behalf. Give direct end users owner-read only and no write grants in that design.

## 3. Create matching buckets and copy a frozen snapshot

- [ ] Work outside the application repository. Set `SKILL_DIR` to this installed
  skill's absolute directory and `TERRASCALE_SDK_DIR` to the SDK source directory.

```bash
umask 077
mkdir storage-migration
cd storage-migration
npm init -y
npm install "$TERRASCALE_SDK_DIR"
cp "$SKILL_DIR/scripts/copy.mjs" copy.mjs
mkdir staging
```

- [ ] Create one dedicated target bucket per source access boundary, including
  empty source buckets. Run this server-side as `create-bucket.mjs`; supply
  `TERRASCALE_PROJECT_ID`, `TERRASCALE_API_KEY` and `SOURCE_BUCKET` privately.
  This uses the [documented bucket API](https://docs.terrascale.tech/guides/file-storage)
  and [current client setup](https://docs.terrascale.tech/getting-started/javascript).

```js
import { createTerraBaseV1Client } from '@terrascale/sdk';
const admin = createTerraBaseV1Client({
  project: process.env.TERRASCALE_PROJECT_ID,
  apiKey: process.env.TERRASCALE_API_KEY,
  baseUrl: 'https://api.terrascale.tech',
});
const bucket = await admin.storage.createBucket(process.env.SOURCE_BUCKET, {
  owner: '/userId',
  read: { owner: true, public: false, roles: [] },
  write: { owner: false, public: false, roles: [] },
});
console.log(bucket.id);
```

```bash
node create-bucket.mjs
```

Record the returned nonsecret bucket ID. Never recreate a bucket just because a
response was lost: page through `admin.storage.listBuckets({ after })` until
`next` is null and reconcile name/rules first. The owner pointer is fixed for a
bucket's lifetime; see [file storage](https://docs.terrascale.tech/guides/file-storage).

- [ ] Configure the rclone source using environment variables; supply the access
  key, secret key, endpoint and region variables through your secret manager.
  See [rclone S3](https://rclone.org/s3/),
  [Supabase S3 compatibility](https://supabase.com/docs/guides/storage/s3/compatibility)
  and [rclone copy](https://rclone.org/commands/rclone_copy/).

```bash
export RCLONE_CONFIG_SOURCE_TYPE=s3
export RCLONE_CONFIG_SOURCE_PROVIDER=Other
export RCLONE_CONFIG_SOURCE_ACCESS_KEY_ID="$SUPABASE_S3_ACCESS_KEY_ID"
export RCLONE_CONFIG_SOURCE_SECRET_ACCESS_KEY="$SUPABASE_S3_SECRET_ACCESS_KEY"
export RCLONE_CONFIG_SOURCE_ENDPOINT="$SUPABASE_S3_ENDPOINT"
export RCLONE_CONFIG_SOURCE_REGION="$SUPABASE_S3_REGION"
rclone copy "source:$SOURCE_BUCKET" "staging/$SOURCE_BUCKET" --dry-run
rclone copy "source:$SOURCE_BUCKET" "staging/$SOURCE_BUCKET"
rclone check "source:$SOURCE_BUCKET" "staging/$SOURCE_BUCKET" --download
rclone size "source:$SOURCE_BUCKET" --json > source-size.json
```

Repeat for every bucket and retain each size report separately. `copy` can be
rerun to skip unchanged files; `check --download` checks content without relying
on S3 ETags. A nonzero exit blocks migration. If listing fails, apply the
Supabase compatibility page's rclone listing guidance; never accept partial lists.
Use a fresh staging snapshot after source deletes, since `copy` retains old local
files. Object keys must be representable as distinct local paths; detect dot
segments, case collisions and file/directory conflicts before copying. If a key
cannot round-trip exactly, stop and use an approved key-renaming map and rewrite
all references, or keep that object on the source.

- [ ] Build `manifest.json` from the frozen object inventory and approved identity
  and bucket maps. Use exact original names, byte sizes and MIME values, not
  local filename guesses. One filled, nonsecret example:

```json
[
  {
    "bucket": "avatars",
    "name": "old-user-17/photo.png",
    "bucketId": "01ARZ3NDEKTSV4RRFFQ69G5FAV",
    "size": 6,
    "contentType": "image/png",
    "userId": "01ARZ3NDEKTSV4RRFFQ69G5FAW"
  }
]
```

The six-byte entry illustrates the manifest shape, not an image fixture. Replace
the ID and size with actual inventory values. Reconcile manifest counts/bytes
per bucket against SQL and `rclone size`, including zero-object buckets. Every
source row must appear exactly once before applying.

```bash
node copy.mjs manifest.json staging state.json --dry-run
node copy.mjs manifest.json staging state.json --apply
```

The [copy script](scripts/copy.mjs) validates all local files first, snapshots
checksums, checkpoints each allocation, completes signed uploads and downloads
every target object to verify SHA-256 and size. It paginates target lists and
compares counts, byte totals, MIME and ownership metadata. Only use dedicated
target buckets and one script process per state file; do not edit sources while
it runs. A successful rerun revalidates existing IDs rather than creating copies.

For transfer expiry, lost responses and partial runs, follow
[recovery and URL mapping](references/recovery-and-urls.md). An uncertain
checkpoint stops the run; it never blindly retries a mutation. The public
[signed-transfer contract](https://docs.terrascale.tech/guides/file-storage) gives
five minutes to complete uploads and 60 seconds for download capabilities.

## 4. Replace URLs and verify application access

- [ ] Convert each source bucket/key to `{bucketId, fileId}` using `state.json`.
  Replace `getPublicUrl` with the approved publishing gateway or an authorized
  download flow. Replace `createSignedUrl` with `bucket.downloadUrl(fileId)` and
  issue a fresh capability at access time. Never store signed URLs in rows or logs.
- [ ] Rewrite URL columns and JSON references using an explicit mapping table,
  retaining old values for rollback. Use the concrete parameterized example in
  [recovery and URLs](references/recovery-and-urls.md). Do not perform a global
  hostname replacement: target object IDs differ from source paths.
- [ ] Update browser/mobile code to use the documented end-user token provider;
  never expose the migration credential. Target downloads are attachments with
  octet-stream content type; image rendering needs a reviewed application-serving
  path. These behaviors are documented in
  [file storage](https://docs.terrascale.tech/guides/file-storage).
- [ ] Exercise owner, unrelated user, matching role, wrong role and anonymous
  access for list/download/upload/delete. Check policy-denied operations and
  source MIME/size restrictions through the gateway. Verify a multi-page bucket.
- [ ] Confirm source counts/bytes, manifest counts/bytes, target counts/bytes and
  all downloaded checksums agree. Check every rewritten DB reference resolves;
  empty target buckets must list zero objects. Any failure blocks cutover.

## 5. Cut over with a write freeze

- [ ] Freeze all source uploads, overwrites, deletes and relevant DB row writes.
  Drain in-flight jobs; archive the previous snapshot and take a fresh inventory.
  Follow [final-delta reconciliation](references/final-delta.md): dry-run the
  changed/deleted-key plan, retire obsolete target IDs, prune only their live
  checkpoint entries, allocate replacements and verify the exact final inventory.
  Preserve the prior mappings and source bytes in the protected archive.
- [ ] Before accepting target writes, implement the durable create/replace/delete
  and reference-change journal in [rollback reconciliation](references/rollback.md).
  All writers, including jobs, administrators and direct-client paths, must
  participate or remain disabled. Prove replacement and deletion rollback first.
- [ ] Switch application reads and writes and the DB reference map together.
  Release the freeze only after the access matrix and URL checks pass.
- [ ] Monitor denied requests, missing files, gateway errors and shared usage.
  Keep source buckets read-only and retain original DB URL values as recovery
  evidence through the agreed rollback window; current references and tombstones
  come from the journal. Do not revoke source credentials prematurely.

## 6. Roll back and retire

- [ ] Freeze target and source writers, drain work, and reconcile every committed
  create, replacement, deletion and reference change into the rollback destination
  using [the journal replay procedure](references/rollback.md). Keep the freeze
  if any operation or uncertain mutation is unresolved. Never restore all original
  URLs unconditionally: doing so can resurrect deleted data or discard replacements.
- [ ] Verify current source bytes, ownership, references and deletion tombstones
  through the final journal sequence, then switch to the retained source release
  and resume source writers. Invalidate application/CDN caches of deleted or
  replaced files as part of that verification.
- [ ] After successful acceptance and the rollback window, delete target copies
  only when abandoning the migration, or retire the source when keeping it.
  Target file deletion queues cleanup; bucket deletion can return 409 until
  completed files are deleted and pending uploads expire. Outstanding download
  capabilities retain their short lifetime, as documented in
  [cleanup and signed transfers](https://docs.terrascale.tech/guides/file-storage).
- [ ] Remove temporary credentials, protected staging bytes and manifests under
  the project's retention policy. Preserve a nonsecret counts/checksums report.
