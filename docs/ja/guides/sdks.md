---
title: SDK reference
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# SDK reference

Use a TerraBase SDK to store and query JSON documents in a project database. Each SDK calls the project-scoped Documents API at `https://api.terrascale.tech`.

## Languages and installation

| Language | Package / entrypoint | Runtime | Install |
| --- | --- | --- | --- |
| JavaScript / TypeScript | `@terrascale/sdk@0.0.17`, `createTerraBaseV1Client` | Node 22 or 24; npm ≥11; bundled TypeScript declarations | npm; [quickstart](/ja/getting-started/javascript) |
| C# | `TerraScale.DotNet`, `TerraBaseDocumentsClient` | .NET 10; source also targets .NET 11 with that toolchain | Build the package from source; [quickstart](/ja/getting-started/csharp) |
| Go | `terrascale.tech/sdk/v1`, `tb.New(tb.Config{TokenSource})` | Go ≥1.25 | `go get terrascale.tech/sdk@v0.0.8` |
| Python | `terrascale`, `TerraScale` / `AsyncTerraScale` | Python ≥3.10, httpx | `pip install` from Git; [quickstart](/ja/getting-started/python) |
| Rust | `terrascale::v1`, `Client::for_project` | Rust 2021 edition, Tokio | Git tag `v0.0.2`; not on crates.io yet |

The npm package declares the latest major Chrome, Firefox, Safari and Edge. Its React, Svelte and TanStack entrypoints have their own peer package requirements. There is no Java SDK yet; call the HTTP API directly from those languages.

### JavaScript / TypeScript

```bash
npm install --save-exact @terrascale/sdk@0.0.17
```

The package is ESM-only: use `import` from an ES module (`.mjs`, or `"type": "module"` in `package.json`). `require("@terrascale/sdk")` fails with `ERR_PACKAGE_PATH_NOT_EXPORTED`; from CommonJS, load it with `await import("@terrascale/sdk")` inside an async function.

Import from `@terrascale/sdk`. The root exposes `createTerraBaseV1Client`, `v1Filter` and typed errors. `client.database(name).collection(name)` selects a collection. It provides `create`, `get`, `put`, `patch`, `delete`, `query`, `pages` and `items`; `db.execute`, `db.query` and `db.batch` run [SQL](/ja/guides/sql-http#from-the-sdks). See [the complete example](/ja/getting-started/javascript).

Generate [TypeScript document models](/ja/guides/schema-validation#generate-typescript-document-models) with the Node-only `@terrascale/sdk/codegen` API or `terrascale-models` CLI.

#### JavaScript public operation reference

This compact inventory records published `@terrascale/sdk@0.0.13` exports and returned SDK handles. The installed `.d.ts` files define argument/result types; selecting a handle does not provision it. Constructors are exported, but obtain service handles through the factories. Availability still requires the owning service, scope and role; export presence is not live qualification. Mutations are sent once; reconcile ambiguous outcomes before replay. Reuse an authorized endpoint until automatic eligible-region discovery ships.

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

#### Named identity, framework and admission entrypoints

Imports below use the `@terrascale/sdk` prefix. Framework utilities do not supply a missing producer protocol. The native database-view/TanStack surfaces and local/Postgres runtimes remain inventoried legacy support, without activation instructions.

| Entrypoint / handle | Exports, members and return handles |
| --- | --- |
| `/admin` (explicit administrator JWT) | `createTerraScaleAdministrationClient`; `requireTerraScaleAdministration`; `TerraScaleCapabilityUnavailableError`: `code, capability, required, name`; returns `V1Platform`; deprecated `requireTerraScaleAdministration` delegates to the factory |
| `/management` (explicit project credentials) | `createTerraScaleManagementClient`; `requireTerraScaleManagement`; `TerraScaleCapabilityUnavailableError`: `code, capability, required, name`; `terraScaleReleaseBlockers`: `management, betterAuth, qualification`; returns `databases`, `database(name)`, `metrics`, `backupDestination`, `savedQueries` as root handles; deprecated `requireTerraScaleManagement` delegates to the factory |
| Platform identity factories (`/management`) | `createTerraScaleIdentityManagementClient`; `createTerraScaleAccountDeletionClient`; `createTerraScaleProjectIdentityClient`; returned methods below; platform sign-in JWT, deletion receipt and project API key are distinct authorities |
| `createTerraScaleIdentityManagementClient()` | `lookupInvitation, acceptInvitation, declineInvitation, getAccountMfa, accountLinkedProviders, accountUnlinkProvider, deleteAccount, createOrganization, listOrganizations, getOrganization, renameOrganization, deleteOrganization, listMembers, changeMemberRole, removeMember, leaveOrganization, createInvitation, listInvitations, revokeInvitation, startOwnershipTransfer, getOwnershipTransfer, cancelOwnershipTransfer, confirmOwnershipTransfer, getSsoConnection, putSsoConnection, deleteSsoConnection, verifySsoDomain, setOrganizationMfaPolicy, listAuditEvents, createProject, listProjects, getProject, renameProject, deleteProject, setProjectAllowedCidrs, listProjectMembers, setProjectMemberRole, removeProjectMember, createOrganizationApiKey, listOrganizationApiKeys, getOrganizationApiKey, revokeOrganizationApiKey, rotateOrganizationApiKey, setOrganizationApiKeyAllowedCidrs, createProjectApiKey, listProjectApiKeys, getProjectApiKey, revokeProjectApiKey, rotateProjectApiKey, setProjectApiKeyAllowedCidrs` |
| `createTerraScaleAccountDeletionClient()` | `accountDeletionStatus, accountDeletionHistory` |
| `createTerraScaleProjectIdentityClient()` | `mintEndUserToken` |
| `/ts-auth` factories / patch / verification | `createTsAuthClient`; `TsAuthClient`: `users, sessions, apiKeys, organizations, connections, directories, jwtTemplates, webhooks, auditEvents, billing, instance, oauthClients, oauthResources, oidc, config`; `TsAuthUserPatch`: `withName, unsetName, withUsername, unsetUsername, withPassword, unsetPassword, withEmail, withBanned, withBanReason, unsetBanReason, withPublicMetadata, unsetPublicMetadata, withPrivateMetadata, unsetPrivateMetadata, toJSON`; `verifyTsAuthWebhook`; `verifyTsAuthWebhookWithTolerance`; `createTsAuthClient()` → `TsAuthClient`; methods return typed result unions: inspect `ok` before `value/error` |
| Hosted users / sessions / keys | `TsAuthUsersClient`: `list, listAll, get, create, update, delete, impersonate, sessions, revokeAllSessions, mfaFactors, deleteMfaFactor, regenerateRecoveryCodes, passkeys, deletePasskey, organizations`; `TsAuthSessionsClient`: `revoke`; `TsAuthApiKeysClient`: `create, list, delete`; `auth.users/sessions/apiKeys` |
| Hosted organizations / connections / directories | `TsAuthOrganizationsClient`: `create, list, get, update, delete, createMembership, listMemberships, updateMembership, deleteMembership, createInvitation, listInvitations, revokeInvitation, createDomain, listDomains, deleteDomain, enrollDomain`; `TsAuthConnectionsClient`: `create, list, update, delete`; `TsAuthDirectoriesClient`: `create, list, rotate, delete`; `auth.organizations/connections/directories` |
| Hosted JWT / webhooks / audit / billing / instance | `TsAuthJwtTemplatesClient`: `create, list, update, delete, issueToken`; `TsAuthWebhooksClient`: `create, list, delete, test, deadLetter, replayDeadLetter`; `TsAuthAuditEventsClient`: `list`; `TsAuthBillingClient`: `get, checkout, portal, enforceCheck, reconcileQuota`; `TsAuthInstanceClient`: `get, update`; `auth.jwtTemplates/webhooks/auditEvents/billing/instance`; hosted billing is distinct from unavailable platform billing REST |
| Hosted OAuth / OIDC | `TsAuthOauthClientsClient`: `create, list, update, delete`; `TsAuthOauthResourcesClient`: `create, list, update, revoke`; `TsAuthOidcClient`: `discovery, jwks, authorizationUrl, deviceAuthorization, token, introspect, introspectResource, revoke, userinfo`; `auth.oauthClients/oauthResources/oidc` |
| `/react` hooks / observers | `useQuery`; `useMutation`; `disposeMutation`; `createReactiveHttpQuery`; `ReactiveQueryObserver`: `getSnapshot, getServerSnapshot, updateOptions, subscribe, start, dispose, refetch`; `ReactiveMutationObserver`: `updateOptions, getSnapshot, subscribe, mutateAsync, mutate, cancel, reset, dispose`; `ReactiveMutationError`: `name, code, cause`; `useQuery()` adds `refetch`; `useMutation()` adds `mutate/mutateAsync/cancel/reset`; query sources expose `subscribe` and return cleanup |
| `/svelte` stores / shared exports | `createQuery`; `createMutation`; `createReactiveHttpQuery`; `ReactiveQueryObserver`: `getSnapshot, getServerSnapshot, updateOptions, subscribe, start, dispose, refetch`; `ReactiveMutationObserver`: `updateOptions, getSnapshot, subscribe, mutateAsync, mutate, cancel, reset, dispose`; `ReactiveMutationError`: `name, code, cause`; shared observer exports match `/react`; query store returns `subscribe/refetch`, mutation store `subscribe/mutate/mutateAsync/cancel/reset`; `subscribe()` returns unsubscribe function |
| Deprecated native view exports (`/react`, `/svelte`) | `createDatabaseQuery`; `DatabaseSubscriptionView`: `binding, getSnapshot, getRequest, restart, apply`; `DatabaseSubscriptionError`: `name, code, detail`; same exports in `/svelte`; `createDatabaseQuery()` returns `key/binding/subscribe`; no current producer qualification |
| Legacy native `/tanstack` integration | `terrascaleCollectionOptions`; `TerraScaleTanStackCollectionPublisher`: `getPosition, reset, dispose, publish`; `TerraScaleTanStackCollectionSync`: `options, getCheckpoint, resubscribe, dispose`; `TerraScaleTanStackMutationError`: `name, detail, code`; returns `adapter/getKey/sync.sync/onInsert/onUpdate/onDelete`; `sync.sync()` returns cleanup; native transport is outside current v1 onboarding |
| Unavailable server-only `/better-auth` | `requireTerraScaleBetterAuth`; `TerraScaleCapabilityUnavailableError`: `code, capability, required, name`; `terraScaleReleaseBlockers`: `management, betterAuth, qualification`; guard throws; no supported production adapter until named auth primitives, durable receipts and qualification are admitted |
| Deprecated `/local` and `/postgres` | `createTerraScaleSqliteRuntime`; `TerraScaleSqliteRuntime`: `scope, databasePath, address, authority, bindCollection, createClient, createBetterAuthAdapter, start, stop, reset, close`; `PostgresClient`: `query, close, static connect`; `PostgresError`: `name, kind, sqlState`; `postgresQuery`; `terrascale-local` is a deprecated legacy CLI; no activation guidance |
| Remaining unavailable admissions | Durable schema plan/apply/status, public platform billing REST, platform administration mutations, Better Auth persistence and its qualification. Existing named management methods above do not imply these missing operations exist. |

### C#

`TerraScale.DotNet` is not on nuget.org yet. Build version `0.0.12` from source revision `e786f2e38b34985293446fd1eb5bfa602e041b85` into a local package feed, as shown in the [C# quickstart](/ja/getting-started/csharp).

`Database(projectId, databaseName)` returns a handle with `CreateDocumentAsync`, `GetDocumentAsync`, `PutDocumentAsync`, `PatchDocumentAsync`, `DeleteDocumentAsync`, `QueryAsync` and `TransactAsync`; `ExecuteSqlAsync`, `QuerySqlAsync` and `ExecuteSqlBatchAsync` run [SQL](/ja/guides/sql-http#from-the-sdks). A caller-owned `HttpClient` must disable redirects and cookies; use `TerraBaseHttp.CreateHandler()` and dispose the transport yourself.

### Go

```bash
go get terrascale.tech/sdk@v0.0.8
```

Import `tb "terrascale.tech/sdk/v1"` and `terrascale.tech/sdk/tsauth`. `tsauth.NewAPIKeySource` exchanges the API key for short-lived access tokens and renews them; `tb.New(tb.Config{TokenSource: tokens})` uses `https://api.terrascale.tech` by default. `Database(projectId, name)` and named collections scope document operations; `ExecuteSQL`, `QuerySQL` and `BatchSQL` run [SQL](/ja/guides/sql-http#from-the-sdks).

```go
tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: apiKey, Project: projectID})
if err != nil {
	return err
}
client, err := tb.New(tb.Config{TokenSource: tokens})
if err != nil {
	return err
}
posts := client.Database(projectID, databaseName).Collection("posts")
created, err := posts.Create(ctx, map[string]any{"title": "Hello"})
```

See the complete [Go quickstart](/ja/getting-started/go).

### Python

```bash
pip install "terrascale @ git+https://github.com/TerraScale/ts-python@v0.0.1"
```

`TerraScale()` takes the API key and project ID (or reads `TERRASCALE_API_KEY` and `TERRASCALE_PROJECT_ID`), exchanges the key for access tokens and refreshes them. `ts.database(name).collection(name)` provides `create`, `get`, `put`, `patch`, `delete`, `list`, `query`, `pages`, `items`, `search` (vector) and the TTL methods; the database handle runs SQL and transactions. `AsyncTerraScale` is the asyncio twin. See the [Python quickstart](/ja/getting-started/python).

### Rust

The `terrascale` crate is not on crates.io yet. Add version `0.0.2` from its tag in the public [ts-rust](https://github.com/TerraScale/ts-rust) repository:

```bash
cargo add terrascale --git https://github.com/TerraScale/ts-rust --tag v0.0.2
cargo add tokio -F tokio/full
cargo add serde_json
cargo add serde --features derive
```

Use `terrascale::v1`. `Client::for_project(projectId, accessToken).build()?` sends requests to `https://api.terrascale.tech`. `client.database(name).collection(name)` provides `get`, `put`, `create`, `patch`, `delete`, `query` and `pages`; `database.sql()` runs [SQL](/ja/guides/sql-http#from-the-sdks) with `execute`, `query` and `batch`. The token is the access token from the [key exchange](/ja/guides/authentication).

## .NET and Go feature availability

Match the installed artifact to the example. A source method or HTTP fixture does not establish production qualification; each call still needs the owning service, project scope and role.

| Artifact | Full-text / vector queries | Streaming document export | Installation |
| --- | --- | --- | --- |
| Go `v0.0.5` ([source](https://github.com/TerraScale/ts-go/tree/b2b7a55a66da4dd0db6588d27d571efe2177f31c)) | Included | Not included | Historical published module |
| Go source [`416faaad8787ef883b7ff0be3bbbb90c5b0f8567`](https://github.com/TerraScale/ts-go/tree/416faaad8787ef883b7ff0be3bbbb90c5b0f8567) | Included | `Database.Export` | `go get terrascale.tech/sdk@416faaad8787ef883b7ff0be3bbbb90c5b0f8567`; Go records its immutable pseudo-version |
| .NET source [`e786f2e38b34985293446fd1eb5bfa602e041b85`](https://github.com/TerraScale/ts-dotnet/tree/e786f2e38b34985293446fd1eb5bfa602e041b85), local pack `0.0.12` | Included | `TerraBaseDatabase.ExportAsync` | Source/local feed only; used by the C# quickstart |

Export streams documents, not schemas, indexes or a database backup. Stop or cancel iteration to release the response; exhaust it to establish completion. See [export](/ja/guides/export). These pins do not promise complete SDK feature parity. Newer source may contain additional methods; use its own README and contract fixtures, and preserve the exact revision in your build.

## Contract and routing

The current public contract uses project IDs, database and collection names, JSON documents, opaque versions, JSON filter queries and database-scoped transactions. Older SQL, UUID/shard and reactive-view examples are separate contracts. Do not mix their methods or credentials with this API.

For the current JavaScript artifact, collection routes are rooted at:

```text
/v1/projects/{project}/databases/{database}/collections/{collection}
```

`POST /documents` creates a server-generated document ID. `GET`, `PUT`, `PATCH` and `DELETE /documents/by-key?id={id}` address a document. Document ids are canonical ULIDs: 26 uppercase Crockford base32 characters. Any other id answers `400 invalid_document_id`. Omit the id on create and TerraBase generates one. `POST /query` returns one cursor page. Use the SDK's named operations to encode identifiers and validate responses.

Configure exactly one HTTPS endpoint or resolver, normally `https://api.terrascale.tech`. The SDKs do not discover endpoints or fail over between regions on their own.

## Credentials, retries and compatibility

Each SDK sends an access token as `Authorization: Bearer`. Exchange your dashboard API key for that token first; the key's project, database and permissions decide what the token can do. See [authentication](/ja/guides/authentication).

JavaScript and C# send each request once. They do not provide mutation deduplication; `Idempotency-Key` is unsupported in this contract. Go has bounded retries for safe operations. Preserve versions and reconcile uncertain writes rather than blindly replaying them. See [errors](/ja/guides/errors).

</div>
