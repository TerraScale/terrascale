# Project file buckets

`TerraBaseDocumentsClient.Project(projectId)` now exposes CreateFileBucketAsync, ListFileBucketsAsync, SetFileBucketRulesAsync, DeleteFileBucketAsync, CreateFileUploadAsync, CompleteFileUploadAsync, ListBucketFilesAsync, CreateFileDownloadAsync and DeleteBucketFileAsync.

The additive v1 producer contract is [file-buckets.md](https://github.com/TerraScale/terrascale/blob/1c94fe262795ea87f55248989e04f5b93f2f40bf/docs/terrabase/file-buckets.md), PR https://github.com/TerraScale/terrascale/pull/602. This SDK does not run a TerraBase binary in tests; HTTP fakes check project authority, routes, scopes, payloads, pagination, errors and no replay of POSTs.

Requests use the same project/end-user access token provider as document calls, with documents:read or documents:write. Bucket administration requires unrestricted project credentials. End-user owner/role rules apply to file operations. All generated IDs are ULIDs; upload size is 1–100 MiB and SHA-256 is base64. Contents and metadata are immutable.

CreateFileUploadAsync reserves shared database/file storage and returns a five-minute PUT capability. Upload the exact bytes with all returned headers using a separate HTTP client with no credentials, cookies or automatic redirects. CompleteFileUploadAsync verifies the object before reads. CreateFileDownloadAsync returns a 60-second attachment capability. Never log signed URLs. Permissions apply at capability issuance, and existing capabilities last until expiry. FileBytes is included in project usage totals; replication adds no storage charge.
