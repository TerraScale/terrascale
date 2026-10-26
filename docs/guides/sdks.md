---
title: SDK reference
---

# SDK reference

Use a TerraBase SDK to store and query JSON documents in a project database. Each SDK calls the project-scoped Documents API at `https://api.terrascale.tech`.

## Languages and installation

| Language | Package / entrypoint | Runtime | Install |
| --- | --- | --- | --- |
| JavaScript / TypeScript | `@terrascale/sdk@0.0.13`, `createTerraBaseV1Client` | Node 22 or 24; npm ≥11; bundled TypeScript declarations | npm; [quickstart](/getting-started/javascript) |
| C# | `TerraScale.DotNet`, `TerraBaseDocumentsClient` | .NET 10; source also targets .NET 11 with that toolchain | Build the package from source; [quickstart](/getting-started/csharp) |
| Go | `terrascale.tech/sdk/v1`, `New(Config)` | Go ≥1.25 | `go get terrascale.tech/sdk@v0.0.3` |
| Python | `terrascale`, `TerraScale` / `AsyncTerraScale` | Python ≥3.10, httpx | `pip install` from Git; [quickstart](/getting-started/python) |
| Rust | `terrascale::v1`, `Client::for_project` | Rust 2021 edition, Tokio | Git tag `v0.0.2`; not on crates.io yet |

The npm package declares the latest major Chrome, Firefox, Safari and Edge. Its React, Svelte and TanStack entrypoints have their own peer package requirements. There is no Java SDK yet; call the HTTP API directly from those languages.

### JavaScript / TypeScript

```bash
npm install --save-exact @terrascale/sdk@0.0.13
```

Import from `@terrascale/sdk`. The root exposes `createTerraBaseV1Client`, `v1Filter` and typed errors. `client.database(name).collection(name)` selects a collection. It provides `create`, `get`, `put`, `patch`, `delete`, `query`, `pages` and `items`; `db.execute`, `db.query` and `db.batch` run [SQL](/guides/sql-http#from-the-sdks). See [the complete example](/getting-started/javascript).

Generate [TypeScript document models](/guides/schema-validation#generate-typescript-document-models) with the Node-only `@terrascale/sdk/codegen` API or `terrascale-models` CLI.

#### JavaScript public operation reference

This compact inventory covers published `@terrascale/sdk@0.0.13` exports and returned SDK handles. The installed `.d.ts` files define argument/result types; selecting a handle does not provision it. Constructors are exported, but obtain service handles through the factories. Availability still requires the owning service, scope and role; export presence is not live qualification. Mutations are sent once; reconcile ambiguous outcomes before replay. Reuse an authorized endpoint until automatic eligible-region discovery ships.

| Entrypoint / handle | Exports, members and return handles |
| --- | --- |
| Root factories and aliases | `createTerraBaseV1Client`; `createTerraBaseClient`; `createTerraBaseSystemClient`; `createTerraBaseClient = createTerraBaseV1Client`; system factory returns `health: V1Health`, `platform: V1Platform` |
| Root client | `TerraBaseV1Client`: `database, databases, storage, savedQueries, metrics, backupDestination, health, platform`; `TerraBaseClient`: `database, databases, storage, savedQueries, metrics, backupDestination, health, platform`; `TerraBaseClient = TerraBaseV1Client`; `database()` → `V1Database`; other members are named handles below |
| Database / catalogs | `V1Database`: `imports, backups, metrics, indexes, schema, collections, name, collection, transaction, sql, checkSql, explainSql, execute, query, batch, changes, export`; `V1Databases`: `create, list, get, update, delete, branch`; `V1Collections`: `create, get, list, drop`; `collection()` → `V1Collection`; `transaction()` → `V1Transaction`; `sql()` is a method, not a namespace |
| Collection / transaction | `V1Collection`: `webhooks, name, database, get, put, create, patch, delete, query, list, getSchema, putSchema, previewSchema, deleteSchema, getTtl, putTtl, deleteTtl, pages, items, shape, subscribeShape, shapeManifest, shapeKey, decryptShapeObject`; `V1Transaction`: `size, create, put, patch, delete, assert, assertAbsent, assertCount, commit`; `pages/items` return async generators; `changes/export` also stream; `changes()` has own `next/return` wrappers; `subscribeShape()` → `V1ShapeSubscription` |
| Indexes / database declarations | `V1Indexes`: `create, list, drop, getAutoPolicy, setAutoPolicy, recommendations, applyRecommendation`; `V1DatabaseSchemaClient`: `list, infer, declare, queryShapes`; `db.indexes/schema`; JSON Schema uses collection schema methods |
| Backups / destinations | `V1Backups`: `create, list, restore, getPolicy, setPolicy, window, plan, restorePointInTime`; `V1BackupDestinationClient`: `get, set, delete`; `db.backups`, `client.backupDestination` |
| Usage / import / saved queries | `V1DatabaseUsageClient`: `usage, analytics, replication`; `V1ProjectUsageClient`: `usage, analytics, replication`; `V1Imports`: `create, get, uploadSource, update, start, cancel, resume`; `V1SavedQueries`: `create, list, get, update, delete`; `db.metrics/imports`, `client.metrics/savedQueries` |
| Change webhooks / project files | `V1ChangeWebhooks`: `create, list, delete, deliveries`; `V1FileStorage`: `createBucket, listBuckets, bucket`; `V1FileBucket`: `setRules, delete, createUpload, completeUpload, list, downloadUrl, deleteFile, upload, download`; `collection.webhooks`, `client.storage`; `bucket()` → `V1FileBucket`; storage admission is separate |
| Shape state / authorized system reads | `V1ShapeSubscription`: `state, upToDate, done, close`; `V1Health`: `liveness, readiness, backup, replication`; `V1Platform`: `databases, collections, documents, document`; `client.health/platform`; platform reads require administrator role; encrypted object delivery still needs producer admission |
| Filters / IDs / endpoint helper | `v1Filter`: `id, and, or, not, eq, ne, lt, lte, gt, gte, in, nin, exists, prefix, search, contains, withinRadius, withinBox`; `generateId`; `isValidId`; `requireId`; `indexedDbShapeReplica`; `staticBaseUrlResolver`; `indexedDbShapeReplica()` returns replica `load/save` (save `undefined` to clear); endpoint helper does not discover eligible regions |
| Exact numbers / typed errors | `ExactDecimal`: `toString, toJSON, toNumber, isInteger, toBigInt, equals, static from`; `TerraBaseV1Error`: `status, code, op, handle, retryAfter, etag, details, body, responseText, headers, requestId, name`; `SqlError`: `statement, sqliteCode, name, status, code, op, handle, retryAfter, etag, details, body, responseText, headers, requestId`; `V1PreconditionFailedError`: `name, status, code, op, handle, retryAfter, etag, details, body, responseText, headers, requestId`; `V1InvalidKeyError`: `name, status, code, op, handle, retryAfter, etag, details, body, responseText, headers, requestId`; `V1ForbiddenScopeError`: `name, status, code, op, handle, retryAfter, etag, details, body, responseText, headers, requestId`; `V1NotFoundError`: `name, status, code, op, handle, retryAfter, etag, details, body, responseText, headers, requestId`; `isTerraBaseV1Error`; `V1ErrorCode`: `invalidRequest, invalidQuery, invalidCursor, invalidPatch, sortRequiresIndex, assertRequiresIndex, invalidKey, forbiddenScope, notFound, unknownDatabase, unknownCollection, unknownRoute, methodNotAllowed, patchConflict, nameTaken, mustRefetch, preconditionFailed, payloadTooLarge, unsupportedMediaType, uriTooLong, shapeBusy, internalError, capacity, notModified, sqlError, sqlConflict, sqlTooLarge`; error subclass fields include inherited fields |
| Admission metadata (also re-exported below) | `TerraScaleCapabilityUnavailableError`: `code, capability, required, name`; `terraScaleReleaseBlockers`: `management, betterAuth, qualification` |
| `/codegen` (Node only) | `generateDocumentModels`; `generateCollectionModels`; `terrascale-models` CLI; [complete typed-model fixture](/examples/typed-models/README.txt) |
| `/schema` pure declaration helpers | `collection`; `defineField`; `defineSchema`; `encodeSchemaFields`; `normalizeSchema`; `serializeSchema`; `validateCollection`; `validateSchema`; `CollectionBuilder`: `field, build, toJSON`; `SchemaBuilder`: `addCollection, collection, build, validate, toJSON`; `FieldTypes`: `bool, i64, utf8, bytes, map, array`; `SchemaValidationError`: `name`; native field declarations, not the v1 JSON Schema attachment operation |
| `/sql` pure construction / validation | `raw`; `select`; `selectSchema`; `validateSql`; `validate_sql`; `Query`: `statement, parameters, toDescriptor, static raw, static select, static selectSchema`; `SelectBuilder`: `column, columns, from, whereEq, whereNe, whereGt, whereGte, whereLt, whereLte, whereLike, whereIn, orderByField, orderBy, limit, offset, paginate, page, build, toDescriptor, static fromColumns, static fromSchema`; `Direction`: `Asc, Desc`; `SqlValidationError`: `name, code`; `raw/select/selectSchema` return `Query/SelectBuilder/SelectBuilder`; `validate_sql = validateSql`; no execution or listener admission |

### C#

`TerraScale.DotNet` is not on nuget.org yet. Build version `0.0.6` (tag `v0.0.6`) from the public [ts-dotnet](https://github.com/TerraScale/ts-dotnet) source into a local package feed, the way the [C# quickstart](/getting-started/csharp) builds its pinned version.

`Database(projectId, databaseName)` returns a handle with `CreateDocumentAsync`, `GetDocumentAsync`, `PutDocumentAsync`, `PatchDocumentAsync`, `DeleteDocumentAsync`, `QueryAsync` and `TransactAsync`; `ExecuteSqlAsync`, `QuerySqlAsync` and `ExecuteSqlBatchAsync` run [SQL](/guides/sql-http#from-the-sdks). A caller-owned `HttpClient` must disable redirects and cookies; use `TerraBaseHttp.CreateHandler()` and dispose the transport yourself.

### Go

```bash
go get terrascale.tech/sdk@v0.0.3
```

Import `terrascale.tech/sdk/v1`. `New(Config)` takes a `BaseURL` (or a `Resolver`) and a `Token`. `Database(projectId, name)` and named collections scope document operations; `ExecuteSQL`, `QuerySQL` and `BatchSQL` run [SQL](/guides/sql-http#from-the-sdks).

### Python

```bash
pip install "terrascale @ git+https://github.com/TerraScale/ts-python@v0.0.1"
```

`TerraScale()` takes the API key and project ID (or reads `TERRASCALE_API_KEY` and `TERRASCALE_PROJECT_ID`), exchanges the key for access tokens and refreshes them. `ts.database(name).collection(name)` provides `create`, `get`, `put`, `patch`, `delete`, `list`, `query`, `pages`, `items`, `search` (vector) and the TTL methods; the database handle runs SQL and transactions. `AsyncTerraScale` is the asyncio twin. See the [Python quickstart](/getting-started/python).

### Rust

The `terrascale` crate is not on crates.io yet. Add version `0.0.2` from its tag in the public [ts-rust](https://github.com/TerraScale/ts-rust) repository:

```bash
cargo add terrascale --git https://github.com/TerraScale/ts-rust --tag v0.0.2
cargo add tokio -F tokio/full
cargo add serde_json
cargo add serde --features derive
```

Use `terrascale::v1`. `Client::for_project(projectId, accessToken).build()?` sends requests to `https://api.terrascale.tech`. `client.database(name).collection(name)` provides `get`, `put`, `create`, `patch`, `delete`, `query` and `pages`; `database.sql()` runs [SQL](/guides/sql-http#from-the-sdks) with `execute`, `query` and `batch`. The token is the access token from the [key exchange](/guides/authentication).

## Contract and routing

The current public contract uses project IDs, database and collection names, JSON documents, opaque versions, JSON filter queries and database-scoped transactions. Older SQL, UUID/shard and reactive-view examples are separate contracts. Do not mix their methods or credentials with this API.

For the current JavaScript artifact, collection routes are rooted at:

```text
/v1/projects/{project}/databases/{database}/collections/{collection}
```

`POST /documents` creates a server-generated document ID. `GET`, `PUT`, `PATCH` and `DELETE /documents/by-key?id={id}` address a document. Document ids are canonical ULIDs: 26 uppercase Crockford base32 characters. Any other id answers `400 invalid_document_id`. Omit the id on create and TerraBase generates one. `POST /query` returns one cursor page. Use the SDK's named operations to encode identifiers and validate responses.

Configure exactly one HTTPS endpoint or resolver, normally `https://api.terrascale.tech`. The SDKs do not discover endpoints or fail over between regions on their own.

## Credentials, retries and compatibility

Each SDK sends an access token as `Authorization: Bearer`. Exchange your dashboard API key for that token first; the key's project, database and permissions decide what the token can do. See [authentication](/guides/authentication).

JavaScript and C# send each request once. They do not provide mutation deduplication; `Idempotency-Key` is unsupported in this contract. Go has bounded retries for safe operations. Preserve versions and reconcile uncertain writes rather than blindly replaying them. See [errors](/guides/errors).
