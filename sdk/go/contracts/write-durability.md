# Document write durability

This is a scoped header extension to existing document and transaction operations.
Owning public producer: `TerraScale/terrascale@1d823d8534fc5035859a47e263691d0c7cb7b744`.
Source hashes below pin the route, header, authority, result and error behavior;
HTTP-fake tests are not deployed-service proof.

| Operation | Method | Path below `/v1/projects/{project}/databases/{database}` |
| --- | --- | --- |
| createDocument | POST | `/collections/{collection}/documents` |
| putDocument | PUT | `/collections/{collection}/documents/by-key` |
| patchDocument | PATCH | `/collections/{collection}/documents/by-key` |
| deleteDocument | DELETE | `/collections/{collection}/documents/by-key` |
| runTransaction | POST | `/transactions` |

Existing document IDs, bodies/media types, preconditions and bearer project authority
are unchanged: mutations need `documents:write`; transaction assertions also need
`documents:read`. There is no pagination or checkpoint change.
`TB-Durability` is optional: absent or `local_durable` requests the local disk barrier;
`remote_durable` requests another placed region's durable acknowledgement. Other
values are invalid. Pass `LocalDurable` or `RemoteDurable` as a document WriteOption,
or call `Tx.WithDurability`. Preconditions remain independent. Response-only values
`ObjectDurable` and `Unchanged` cannot select a request guarantee.

Read the returned receipt's `Durability`; a one-machine cell can still answer
`local_durable` for a remote request. Single-region placement may instead produce
409 `placement_needs_two_regions`. Before execution, 503 `remote_region_unavailable`
or `confirmation_busy` carries `Retry-After`. After an ambiguous commit or missing
acknowledgement, 503 `write_outcome_unknown` includes region/machine and must never
replay automatically. No idempotency key or new retry path is introduced. Existing
fixed deadlines, caller cancellation and typed error handling still apply.

Own production receipt/denial/cleanup qualification remains pending ingress
restoration and authorized placement fixtures. This does not claim replication
internals or deployed source identity.

| Producer file under `src/TerraBase/Features/Documents` | SHA256 |
| --- | --- |
| `DocumentRoute.cs` | `6add2f7cc95edd8ddb54be992ffc2999ec22e05d91d1837c1f3643e791d2b6e8` |
| `DocumentModels.cs` | `60151155098ad303588b9533a31653c15a9c745bf24d4c9f6865dcb667cd3206` |
| `CreateDocumentEndpoint.cs` | `54b34b25eb61f2c9ad9464bfd4eb9c90653d9900ad606d45c041c552f9942495` |
| `PutDocumentEndpoint.cs` | `b130e811ccca916ae07d33287fd57eaa0800421bb131e3caa1486792f2ef3ccf` |
| `PatchDocumentEndpoint.cs` | `68ea3acff0b49b62ab94e242046de1cfc0e1cdcef35fbb618054bce23f043903` |
| `DeleteDocumentEndpoint.cs` | `a71f61b2cae9cfaa743aff0cd80394dc3657ff85ad0f647f407d1f349a391b68` |
| `TransactionEndpoint.cs` | `3343c6e66e794bdf356f638c167315036dac6eee5f8d25089cbbacc742e86fc9` |
