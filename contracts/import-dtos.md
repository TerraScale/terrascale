# Import DTO source contract

`ImportOptions` represents the producer's `ImportOptionsBody`: only optional
`exclude` (array of strings) and `collection` (string). Create may omit options;
update requires an options object. Nil/empty exclusions and an empty collection
are omitted by Go; omitted producer options select defaults. A null response
collection decodes to the Go string's empty value. There is no caller-selectable
missing-primary-key policy.

`ImportTable` has exactly `source`, `collection`, `excluded`, `rows`,
`primaryKey`, `columns`, `constraints`, and `issues`. Rows are an int64 count;
primary-key columns remain source metadata. The producer returns no `idPolicy`.
The fixtures in `v1/public_imports_test.go` check exact create/update bodies,
nullable collection decoding and lossless table field round trips. They are
synthetic HTTP contracts, not live imports or producer binaries.

Let D = `/v1/projects/{project}/databases/{database}/import`. Existing operations:

| Identity | Method/path | Success |
|---|---|---|
| createImport | POST D | 201 new, 200 identical declaration |
| getImport | GET D | 200 |
| updateImport | PATCH D | 202 |
| uploadImportSource | PUT D/source?offset=n | 200 |
| startImport | POST D/start | 202 |
| cancelImport | POST D/cancel | 202 |
| resumeImport | POST D/resume | 202 |

JSON requests/responses use camelCase; upload requires application/octet-stream,
Content-Length and an unsigned-decimal offset. Calls require a customer project
Bearer access token and the destination home region. Get requires databases:read;
cancel requires databases:write; other mutations also require documents:write.
Endpoint errors retain the existing error envelope and APIError handling; this
DTO correction changes no statuses, dispatch, retries or cancellation. These
operations return one job, with no pagination or partial-result page. An uncertain
mutation must be reconciled by reading the job before further action.

Owning landed TerraBase source: `49acbd764902660dfd4c8e44dd5933bacd99c7aa`.
This is an immutable source comparison, not a deployed-revision or runtime
failure claim. Global generated-contract/route-manifest refresh remains separate
(TS-1274); all seven identities above match the pinned endpoint declarations and
existing SDK calls. Broad README/XML guidance remains with TS-1267.

| Producer source | SHA256 |
|---|---|
| `src/TerraBase/Features/Import/ImportModels.cs` | `bff10eaa8dabc02bd63892326dea122342aa32aa70c533875356b06d05b43ac7` |
| `src/TerraBase/Features/Import/ImportRoute.cs` | `334e54683c4e3b23cbf277e62f0adee1c17abe03cccdb234ded01052cfb7588f` |
| `src/TerraBase/Features/Import/CreateImportEndpoint.cs` | `4ae4b76b32d29eae8ff3c907e185d61cf1841169c3cc8326236fa3c1c33392d8` |
| `src/TerraBase/Features/Import/GetImportEndpoint.cs` | `acacfdd1779b17c2bb96a893da7d9762917fe0015ecb12a5673669a3223bacff` |
| `src/TerraBase/Features/Import/UpdateImportEndpoint.cs` | `916914230c3e78a96c0c9bd47cddb64e3ff33904bc937a71f1c4bbb0ee78ff60` |
| `src/TerraBase/Features/Import/UploadImportSourceEndpoint.cs` | `db814b28cf7db83eb334ac8f54582976d2bb9da3b9ed989144035c07b8f6ec11` |
| `src/TerraBase/Features/Import/StartImportEndpoint.cs` | `f226cf7deff75f5333c9a17aace115f14359dccf5b00c70fed021950dd32971e` |
| `src/TerraBase/Features/Import/CancelImportEndpoint.cs` | `ce49574f1f219d25976758a08d31577a0e9f6ff9a3c8c4d828ee4a0e1c5bb117` |
| `src/TerraBase/Features/Import/ResumeImportEndpoint.cs` | `ef555c4e4cb2eb7e6f5499712dc1acdfd8495f35a7ed7cf2b7a1b625fe18b03b` |
