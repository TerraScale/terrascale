# Prepared schema10 contract (TS-754 / TS-755)

This SDK source prepares the schema operations against TerraBase PR #118 (retired Forgejo host; no GitHub copy),
exact revision `1c2a7e082829bcebf816691537001f6aba168d9a`, which includes the TS-754 ULID prerequisite.
The owning contract has not landed. This is a prepared source API, not a qualified schema10 release.
Against TerraBase main (2026-10-11) these routes exist and need a ts-auth project token; an API key gets 401.

## API and wire contract

All types below are in `TerraScale.DotNet.TerraBase`. Obtain a `TerraBaseDatabase` from
`TerraBaseDocumentsClient.Database(projectId, databaseName)` and call:

```csharp
var declaration = new TerraBaseUniqueSchemaRequest(Indexes:
[
    new("instance_email", ["instanceId", "email"], Unique: true, Normalization: "trim_lowercase")
]);
TerraBaseUniqueSchema saved = await database.DeclareSchemaAsync("users", declaration, cancellationToken);
TerraBaseCollectionSchema inferred = await database.GetSchemaAsync("users", cancellationToken);
```

Declare each collection's relevant keys before writing its records. Request members are `fields[].name`,
`fields[].unique`, `fields[].normalization`, and `indexes[].name`, `indexes[].fields`,
`indexes[].unique`, `indexes[].normalization`. A unique index here is a schema declaration;
it is not the secondary-index create API.

| Operation | Method and path | Authorization | Success |
| --- | --- | --- | --- |
| `declareCollectionSchema` | `PUT /v1/projects/{project}/databases/{database}/schema/{collection}` | ts-auth project JWT, `databases:write` | 200 `{ "constraints": [{ "name", "fields", "normalization" }] }` |
| `getCollectionSchema` | `GET /v1/projects/{project}/databases/{database}/schema/{collection}` | ts-auth project JWT, `databases:read` | 200 `{ "collection", "documents", "sampledDocuments", "fields": [{ "path", "present", "types" }], "uniqueConstraints" }` |

Both use the HTTPS client listener and JSON (`application/json`). Project is a canonical entity ULID;
database and collection are names. Authentication and project/database narrowing follow the owning
project-token contract. PUT documents 400 validation, 403 insufficient scope, 404 inaccessible/missing
resources, and 409 schema conflicts; GET documents 400 inference-budget/validation and 404 missing
resources, with authentication/scope errors enforced by shared middleware. No pagination or checkpoint
is used. Calls are cancellation-aware, deadline-bound and sent once. Identical declarations are
idempotent; declarations can only change when the collection has no retained document states,
including tombstones.

Source evidence: `Features/Management/Schema/{PutCollectionSchemaEndpoint,GetCollectionSchemaEndpoint,
IUniqueSchemaService,ISchemaService,UniqueSchemaService}.cs`, `Engine/UniqueConstraint.cs`,
`Features/Management/ProjectApiRoute.cs`, and `Tests/TerraBase.Fast/Platform/ApiReferenceTests.cs`.
The producer's real-listener reference test checks generated OpenAPI against its public operation
manifest, including both schema operations and TsAuth scopes. Local SDK checks cover serialization,
required/null response boundaries and conflict parsing. A pass-through recorder also verifies that
both schema methods send the producer's verbs, exact paths, media type and body over HTTPS to the
real pinned listener. That older release returns 404 because it predates these routes; no response
is fabricated. Successful declaration/read-back, populated-collection conflict and generated live
parity coverage still await the new release.

## Guarantees and limits

At most 32 unique keys, with one to four distinct dotted paths each. Missing/null components claim no
key. Arrays/objects are invalid unique values. Keys are bounded to 16 KiB each and 64 KiB per document.
String normalization is `none`, invariant `lowercase`, or Unicode-whitespace trim followed by invariant
`trim_lowercase`. It changes the indexed key, not the stored value, and is not Unicode full case folding.
The compound declaration applies its normalization to every string component.

Local duplicates return HTTP 409 `unique_violation`, surfaced as `TerraBaseConflictException.ErrorCode`.
Transactions also preserve `OperationIndex` and roll back every operation. Updates/deletes release
reservations atomically. A schema change on retained data returns 409 `schema_requires_empty_collection`.

Replicas may independently acknowledge conflicting claims during partitions. After replication, the
smallest UTF-8 document key wins each unique value; a document must win every key to be query-visible.
Retained losers return 409 on point reads and can be changed or deleted. This does not provide globally
exclusive admission or fenced failover before credential effects.

## Auth bootstrap and qualification gates

The current producer schema endpoints require a ts-auth project JWT. Auth's owning persistence contract
requires a TerraBase-issued narrowed bootstrap credential for its own state and forbids self-signed
tokens. The required producer provision/introspection credential contract has been raised on #118.
The SDK supplies neither a self-signing workaround nor an alternate route/storage bypass.

Before schema10 activation, land that credential contract and #117/#118, verify the combined server
release tag, full commit/run and SHA-256, update the real test fixture and run end-to-end schema,
identity/document-key, restart and authorization coverage. Apply canonical entity-selector validation
and the required collection identity response shape together with the qualified fixture; do not keep
an old-ID fallback. Auth additionally requires its approved admission design. Removing automatic
failover establishes its recorded common-authority baseline but does not create a new server guarantee.

Documents calls send a canonical ULID document id, validated with `TerraScaleIds.Validate`, as the
query-encoded `id` of `GET/PUT/PATCH/DELETE …/documents/by-key?id=<ULID>` or as a transaction `id`;
any other id throws `ArgumentException` before a request. The envelope's `Id` owns document identity;
Auth entity payloads must omit `id`. New entity ids use `TerraScaleIdGenerator.NewId`; natural keys use
name-based ULIDs (the first 16 bytes of SHA-256 over an unambiguous input including the collection).
Lists and generated POST creation keep their routes.

No migration, compatibility path, reset, deployment or production data deletion is included here.
Production data deleted: none. Reset/deployment remains gated on approved admission and qualified pins.
