---
title: File storage
description: Store project files with signed transfers, owner and role permissions, and a shared database and file storage allowance.
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# File storage

::: warning Release prerequisite
The file API is being rolled out. These examples use `@terrascale/sdk` 0.0.8 or later and require the file API to be available for your project. Live upload, download and authorization verification is pending.
:::

Projects store files in buckets. Each bucket has owner, read and write rules. Your server creates the bucket; signed-in end users upload and download files when its rules allow them. There are no object-store credentials to configure in your application.

## Create a bucket on your server

Use a project access token with write access, obtained through [Authentication](/ru/guides/authentication). Keep this credential on your server.

```js
import { createTerraBaseClient } from '@terrascale/sdk'

const admin = createTerraBaseClient({
  project: projectId,
  token: serverProjectAccessToken,
  baseUrl: 'https://api.terrascale.tech',
})

const bucket = await admin.storage.createBucket('avatars', {
  owner: '/userId',
  read: { owner: true, public: false, roles: [] },
  write: { owner: true, public: false, roles: [] },
})
// Make bucket.id available to your application. It is not a credential.
```

`owner` is a JSON Pointer into each file's `metadata` object. Here, `metadata.userId` must equal the signed-in user's subject when that user creates a file. The owner pointer stays fixed for the bucket's lifetime. File contents and metadata are immutable; upload a new file to replace one.

Bucket creation, listing, access-rule changes and deletion require unrestricted project credentials. Database-restricted credentials cannot access project files. Never put a project credential in a browser or mobile application.

## Upload and download in your application

Use the ts-auth end-user access token linked to this project and authorized for TerraBase, with its signed subject and trusted roles. An ID token or a token intended only for your own API does not authorize file access. Your token provider should return a current access token for each request.

```js
import { createTerraBaseClient } from '@terrascale/sdk'

const client = createTerraBaseClient({
  project: projectId,
  token: () => getEndUserAccessToken(),
  baseUrl: 'https://api.terrascale.tech',
})
const bucket = client.storage.bucket(bucketId)

// selectedFile is a browser File or Blob; subject comes from the user's session.
const file = await bucket.upload('photo.png', selectedFile, {
  contentType: 'image/png',
  metadata: { userId: subject },
})
const bytes = await bucket.download(file.id) // Blob
const page = await bucket.list()            // { items, next }
await bucket.deleteFile(file.id)
```

`upload()` accepts a `Blob` or `Uint8Array`, takes a private byte snapshot, computes its SHA-256 checksum, requests a signed upload URL, sends the bytes, then verifies completion. The SDK does not forward the project or user token to the signed URL. Files contain 1–104857600 bytes (100 MiB), and metadata is a JSON object of at most 16 KiB.

The API denies a different user access with 403 unless a read or write grant permits that operation. Lists include only readable completed files, with permission filtering applied before pagination. Pass `{ after: page.next }` to continue a list when `next` is non-null. Pages contain up to 50 items. All SDK operations accept an abort signal in their options.

## Owner, public and role grants

Read and write policies are independent. Any matching grant permits that operation; missing grants deny it.

| Grant | Who it permits |
| --- | --- |
| `owner: true` | The end user whose signed subject matches the owner field in file metadata |
| `public: true` | All authenticated end users of the project; it does not allow anonymous access |
| `roles: ['editor']` | End users with a matching trusted ts-auth role; role names are case-sensitive |

For example, let every signed-in user read avatars while only their owner can write:

```js
await admin.storage.bucket(bucketId).setRules({
  owner: '/userId',
  read: { owner: false, public: true, roles: [] },
  write: { owner: true, public: false, roles: [] },
})
```

Role grants must come from the signed token. Adding a role to file metadata does not grant that role.

## Signed transfers

For custom upload code, call `bucket.createUpload({ name, contentType, size, sha256, metadata })`. `sha256` is the base64-encoded SHA-256 of the exact bytes, rather than hex. The response contains `{ id, url, expiresAt, headers }`.

1. PUT the exact bytes to `url` with the returned headers. Browsers supply `Content-Length` themselves. Do not send application credentials or cookies, follow redirects, or log the URL.
2. Call `bucket.completeUpload(id)` before the five-minute upload capability expires. Completion verifies both size and checksum before the file becomes readable.

`bucket.downloadUrl(id)` issues a download capability valid for 60 seconds. Downloads are attachments with an octet-stream content type. Treat every signed URL as a secret: anyone holding it can use it until expiry. Changing rules or deleting a file stops new capabilities; already-issued capabilities retain their short lifetime.

The SDK does not automatically retry mutations or signed transfers. If a response is lost, inspect the file's state before repeating an operation.

## Shared storage allowance and cleanup

Files and database data count together against the organization's existing [plan storage allowance](/ru/pricing). Reserving an upload immediately counts its full declared size. Project usage exposes `fileBytes`, and includes those bytes in `storageBytes` and `logicalBytes`.

Deleting a file prevents new downloads and queues physical cleanup. Expired or failed uploads also need cleanup. Their storage reservations are released after object deletion succeeds, so the usage total can take time to fall. Delete files and wait for cleanup before calling `bucket.delete()`; deleting a bucket with files or reservations returns 409.

| Status | Meaning |
| --- | --- |
| 400 | Invalid name, rules, metadata, checksum or upload details |
| 401 | Missing, expired or invalid access token |
| 403 | The token or bucket rules deny the operation |
| 404 | The bucket or file is unavailable to this project |
| 409 | An upload is incomplete, or a bucket still contains files or reservations |
| 429 | The shared database and file storage allowance is full |
| 503 | File storage or usage accounting is temporarily unavailable |

## HTTP routes

All paths below start with `/v1/projects/{project}` and use Bearer authorization. Read operations and writes require matching project access. End-user authorization additionally evaluates the bucket rules.

| Method | Path | Result |
| --- | --- | --- |
| POST | `/buckets` | 201 bucket, from `{ name, rules }` |
| GET | `/buckets?after=ULID` | `{ items, next }` bucket page; project credentials only |
| PUT | `/buckets/{bucket}/rules` | Bucket with replacement `{ rules }` |
| DELETE | `/buckets/{bucket}` | 204 after an empty bucket is deleted |
| POST | `/buckets/{bucket}/files/upload` | 201 signed upload capability |
| POST | `/buckets/{bucket}/files/{file}/complete` | Verified file metadata |
| GET | `/buckets/{bucket}/files?after=ULID` | `{ items, next }` readable file page |
| POST | `/buckets/{bucket}/files/{file}/download` | Signed download capability |
| DELETE | `/buckets/{bucket}/files/{file}` | 204 when cleanup is queued |

Bucket and file IDs are server-generated ULIDs. Signed URL and file API responses use `Cache-Control: no-store`.

</div>
