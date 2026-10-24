# Project file buckets

Files use `/v1/projects/{project}/buckets`. Requests carry ts-auth project or end-user tokens and the existing `documents:read` / `documents:write` scopes. Database-restricted tokens cannot use project files. Project credentials create/list/delete buckets and replace their access rules. End users upload, list, download and delete only as the bucket policy allows.

## HTTP contract

| Method | Project-relative path | Result |
| --- | --- | --- |
| POST | `/buckets` | 201 bucket; body `{name,rules}` |
| GET | `/buckets?after=ULID` | `{items,next}`, 50 buckets |
| PUT | `/buckets/{bucket}/rules` | bucket; body `{rules}` |
| DELETE | `/buckets/{bucket}` | 204; 409 while files or reservations exist |
| POST | `/buckets/{bucket}/files/upload` | 201 `{id,url,expiresAt,headers}` |
| POST | `/buckets/{bucket}/files/{file}/complete` | verified file metadata |
| GET | `/buckets/{bucket}/files?after=ULID` | `{items,next}`, 50 readable completed files |
| POST | `/buckets/{bucket}/files/{file}/download` | `{id,url,expiresAt,headers}` |
| DELETE | `/buckets/{bucket}/files/{file}` | 204; physical cleanup is asynchronous |

Every generated bucket and file ID is a canonical ULID. Mutations are never automatically replayed. Missing resources return 404; denied rules return 403; invalid input returns 400; pending/incomplete uploads and nonempty bucket deletion return 409; shared quota exhaustion returns 429; unavailable accounting or object storage returns 503. Responses use `Cache-Control: no-store`.

Create a bucket with the same policy shape as document collection rules:

```json
{"name":"avatars","rules":{"owner":"/userId","read":{"owner":true,"public":false,"roles":[]},"write":{"owner":true,"public":false,"roles":[]}}}
```

The owner JSON Pointer addresses the file's `metadata` object. Each end-user upload must name its signed subject as owner. Public means all authenticated end users; trusted roles are case-sensitive. An absent grant denies the operation. File contents and metadata are immutable; replace a file by uploading a new ID. The owner pointer cannot change after bucket creation. List filtering happens before pagination. Project closure/deletion refuses nonempty bucket state.

An upload request supplies `{name,contentType,size,sha256,metadata}`. Size is 1–104857600 bytes, SHA-256 is base64, metadata is a JSON object up to 16 KiB. The API reserves storage, then returns a five-minute PUT capability signed for the declared size, type and checksum. Send every returned header with the exact bytes; browsers supply Content-Length themselves. Complete the upload before expiry. Completion hashes the stored bytes and publishes the file only after verifying size and checksum. Replays of a valid PUT can contain only those same bytes.

Download capabilities last 60 seconds and serve as attachments with an octet-stream type and no caching. Treat signed URLs as secrets. A permission change or deletion prevents new capabilities; capabilities already issued retain their bounded lifetime. Failed and deleted upload reservations remain counted until object-store deletion succeeds. Cleanup starts after upload expiry; deleting files also waits for outstanding download capabilities.

`GET /v1/projects/{project}/usage` includes `fileBytes` and adds those bytes to `logicalBytes` and `storageBytes`. Shared organization quota uses the existing plan policy; pending uploads consume their full reservation. Replication does not add file bytes. No replica transfer is billed as customer storage.

## Production backing

R2 buckets created through `cf` on 2026-10-24: `terrascale-gru-files`, `terrascale-sjc-files`, `terrascale-fra-files`. Logical serving regions stay OCI gru/sjc/fra. R2 does not offer a São Paulo location hint: its default placed the gru bucket in ENAM; sjc uses WNAM and fra WEUR. These are R2 placement hints, not guarantees of OCI-region residency. No OCI region was substituted.

All buckets are private; CORS permits GET/HEAD/PUT for customer browser applications and the signed checksum/content headers, without credentials. CORS grants no object access. The service derives the physical bucket from the bucket's creation region. OCI Vault's existing object-store identity delivery provides credentials; the identity must authorize these three buckets. No new customer configuration or enable switch exists. Production rejects Tigris for app files; Development integration binaries can use their existing Tigris test bucket.
