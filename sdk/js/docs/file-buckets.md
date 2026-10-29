# Project files

`client.storage` uses the same project configuration and ts-auth token as document operations. Projects create buckets with document-style rules; end-user tokens are restricted by those rules. API contract: [producer file buckets](https://github.com/TerraScale/terrascale/blob/1c94fe262795ea87f55248989e04f5b93f2f40bf/docs/terrabase/file-buckets.md).

```js
import { createTerraBaseClient } from "@terrascale/sdk";

const client = createTerraBaseClient({ project, token, baseUrl });
// Use project credentials to create a bucket once.
const info = await client.storage.createBucket("avatars", {
  owner: "/userId",
  read: { owner: true, public: false, roles: [] },
  write: { owner: true, public: false, roles: [] },
});

// Use the signed-in end user's ts-auth token and signed subject in your app.
const bucket = client.storage.bucket(info.id);
const file = await bucket.upload("photo.png", selectedFile, {
  contentType: "image/png", metadata: { userId: subject },
});
const blob = await bucket.download(file.id);
const page = await bucket.list();
await bucket.deleteFile(file.id);
```

Upload accepts a `Blob` or `Uint8Array`, computes SHA-256 over a private snapshot, obtains a signed PUT capability, sends the bytes without the project token, and verifies completion. Files contain 1–100 MiB. Contents and metadata are immutable. A different end user receives 403 unless a public or trusted-role grant allows access.

`createUpload({name,contentType,size,sha256,metadata})` and `completeUpload(id)` support custom transfers. SHA-256 is base64. Send the returned upload headers; browsers supply Content-Length. Upload capabilities expire after five minutes; complete before expiry. `downloadUrl(id)` returns a 60-second download capability. Keep URLs private. Permissions apply when a capability is issued; existing capabilities remain valid until expiry.

`storage.listBuckets({after})` and `bucket.list({after})` return `{items,next}`. Rules filter files before pagination. Project credentials can call `bucket.setRules(rules)` and `bucket.delete()`; delete files and wait for cleanup before deleting a bucket. Database-restricted credentials cannot access project files.

File bytes and pending uploads share the existing organization plan's database storage allowance. Project usage includes `fileBytes`; replication adds no storage charge. Failed uploads and deleted files release their reservation after physical cleanup succeeds. Mutations and object transfers are not retried automatically. All methods accept an abort signal through their options.
