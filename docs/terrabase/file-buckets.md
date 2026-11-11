# Project file buckets

Files use `/v1/projects/{project}/buckets`. Requests carry ts-auth project or end-user tokens and the existing `sql:read` / `sql:write` scopes. Database-restricted tokens cannot use project files. Project credentials create/list/delete buckets and replace their access rules. End users upload, list, download and delete only as the bucket policy allows.

## HTTP contract

| Method | Project-relative path | Result |
| --- | --- | --- |
| POST | `/buckets` | 201 bucket; body `{name,rules}` |
| GET | `/buckets?after=ULID` | `{items,next}`, 50 buckets |
| PUT | `/buckets/{bucket}/rules` | bucket; body `{rules}` |
| DELETE | `/buckets/{bucket}` | 204, erasing the files already deleted; 409 while a completed file or an unexpired upload reservation exists |
| POST | `/buckets/{bucket}/files/upload` | 201 `{id,url,expiresAt,headers}` |
| POST | `/buckets/{bucket}/files/{file}/complete` | verified file metadata |
| GET | `/buckets/{bucket}/files?after=ULID` | `{items,next}`, 50 readable completed files |
| POST | `/buckets/{bucket}/files/{file}/download` | `{id,url,expiresAt,headers}` |
| DELETE | `/buckets/{bucket}/files/{file}` | 204; physical cleanup is asynchronous |

Every generated bucket and file ID is a canonical ULID. Mutations are never automatically replayed. Missing resources return 404; denied rules return 403; invalid input returns 400; pending/incomplete uploads and deletion of a bucket that holds a completed file or an unexpired reservation return 409; shared quota exhaustion returns 429; unavailable accounting or object storage returns 503. Responses use `Cache-Control: no-store`.

Create a bucket with its own access policy:

```json
{"name":"avatars","rules":{"owner":"/userId","read":{"owner":true,"public":false,"roles":[]},"write":{"owner":true,"public":false,"roles":[]}}}
```

The owner JSON Pointer addresses the file's `metadata` object. Each end-user upload must name its signed subject as owner. Public means all authenticated end users; trusted roles are case-sensitive. An absent grant denies the operation. File contents and metadata are immutable; replace a file by uploading a new ID. The owner pointer cannot change after bucket creation. List filtering happens before pagination. A project deletion and a version-1 project closure refuse a project that still has a bucket; a version-2 closure, the account erasure, erases every bucket with its files ([account-project-erasure.md](account-project-erasure.md)).

An upload request supplies `{name,contentType,size,sha256,metadata}`. Size is 1–104857600 bytes, SHA-256 is base64, metadata is a JSON object up to 16 KiB. The API reserves storage, then returns a five-minute PUT capability signed for the declared size, type and checksum. Send every returned header with the exact bytes; browsers supply Content-Length themselves. Complete the upload before expiry. Completion hashes the stored bytes and publishes the file only after verifying size and checksum. Replays of a valid PUT can contain only those same bytes.

Download capabilities last 60 seconds and serve as attachments with an octet-stream type and no caching. Treat signed URLs as secrets. A permission change or deletion prevents new capabilities; capabilities already issued retain their bounded lifetime. Failed and deleted upload reservations remain counted until object-store deletion succeeds. Cleanup starts after upload expiry; deleting files also waits for outstanding download capabilities.

## Deleted files, bucket deletion and late uploads

A deleted file keeps its record and its object for 6 minutes: the upload URL's five minutes plus one, so a PUT that still arrives finds a record that names its object. The sweep (every 30 s, one page of one bucket per pass) then deletes the object and the record. An upload that was never completed is swept the same way one minute after its URL expired.

`DELETE /buckets/{bucket}` does not wait for that hold. It refuses with 409 while the bucket has a completed file or a reservation whose URL has not expired by a minute. Otherwise it erases what is left, the deleted files and the expired reservations: for each page of 100 records it deletes the objects, then removes the records in one transaction, then drops the file kind and the bucket definition in a transaction that sees no record. Download capabilities issued for those deleted files stop working then. It spends at most 10 seconds on objects and answers 503 when more are left; the next request goes on from the records that remain.

An upload URL cannot be revoked, and a completed upload can be replayed until the URL expires. Every record removed while its URL could still be used (expiry plus one minute after the moment the erasure read it, a moment taken before the object was deleted) is replaced in the same transaction by a `file_object_purges/<file>` record that holds the organization, project, bucket, region and that time, and no size, so it is not counted as usage. A pass every 5 s deletes the object of each such record again, 100 records per pass, and removes the record once its time has passed. A file uploaded late therefore lives for seconds, and nothing is left when the URL has expired. Project closure erases files with the same steps.

`GET /v1/projects/{project}/usage` includes `fileBytes` and adds those bytes to `logicalBytes` and `storageBytes`. Shared organization quota uses the existing plan policy; pending uploads consume their full reservation. Replication does not add file bytes. No replica transfer is billed as customer storage.

## Production backing

R2 buckets created through `cf` on 2026-10-24: `terrascale-gru-files`, `terrascale-sjc-files`, `terrascale-fra-files`. Logical serving regions stay OCI gru/sjc/fra. R2 does not offer a São Paulo location hint: its default placed the gru bucket in ENAM; sjc uses WNAM and fra WEUR. These are R2 placement hints, not guarantees of OCI-region residency. No OCI region was substituted.

All buckets are private; CORS permits GET/HEAD/PUT for customer browser applications and the signed checksum/content headers, without credentials. CORS grants no object access. The service derives the physical bucket from the bucket's creation region. OCI Vault's existing object-store identity delivery provides credentials; the identity must authorize these three buckets. No new customer configuration or enable switch exists. Production rejects Tigris for app files; Development integration binaries can use their existing Tigris test bucket.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
