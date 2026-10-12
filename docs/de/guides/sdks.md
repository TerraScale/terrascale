---
title: SDK reference
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# SDK reference

Use the project-scoped TerraBase Documents API for new document applications. SDK installation, source implementation and deployed service compatibility are separate facts.

## Languages and installation

Availability checked **October 12, 2026 (UTC)** against the public registries and committed SDK source.

| Language | Package / entrypoint | Runtime | Current evidence |
| --- | --- | --- | --- |
| JavaScript / TypeScript | `@terrascale/sdk@0.0.3`, `createTerraBaseV1Client` | Node 22 or 24; npm ≥11; bundled TypeScript declarations | Published npm artifact; [quickstart](/de/getting-started/javascript) |
| C# | `TerraScale.DotNet`, `TerraBaseDocumentsClient` | .NET 10; source also targets .NET 11 with that toolchain | Public NuGet package endpoint returns 404; exact candidate package required |
| Go | `terrascale.tech/sdk/v1`, `New(Config)` | Go ≥1.25 | Source candidate; public immutable module availability and customer journey not established here |

The npm package declares the latest major Chrome, Firefox, Safari and Edge. Its React, Svelte and TanStack entrypoints have their own package requirements; they do not establish live-service parity with the Documents examples. No public Python, Java or Rust SDK release is verified for this contract.

### JavaScript / TypeScript

```bash
npm install --save-exact @terrascale/sdk@0.0.3
```

Import from `@terrascale/sdk`. The root exposes `createTerraBaseV1Client`, `v1Filter` and typed errors. `client.database(name).collection(name)` selects a collection. It provides `create`, `get`, `put`, `patch`, `delete`, `query`, `pages` and `items`. See [the complete example](/de/getting-started/javascript).

### C#

Obtain a verified immutable package/feed containing `TerraBaseDocumentsClient` and its exact version. The [C# quickstart](/de/getting-started/csharp) shows the package-only build command. Do not present the source project's `0.1.0` value as proof of a public release.

`Database(projectId, databaseName)` returns a handle with `CreateDocumentAsync`, `GetDocumentAsync`, `PutDocumentAsync`, `PatchDocumentAsync`, `DeleteDocumentAsync`, `QueryAsync` and `TransactAsync`. A caller-owned `HttpClient` must disable redirects and cookies; use `TerraBaseHttp.CreateHandler()` and dispose the transport yourself.

### Go

The source candidate uses module `terrascale.tech/sdk`, imported through `terrascale.tech/sdk/v1`. Its `New(Config)` client takes an explicit `BaseURL` or `Resolver`, plus `Token`. `Database(projectId, name)` and named collections scope document operations. Pin an immutable verified module version before integration; this page does not provide an unverified `go get ...@latest` command.

## Contract and routing

The current public contract uses project IDs, database and collection names, JSON documents, opaque versions, JSON filter queries and database-scoped transactions. Older SQL, UUID/shard and reactive-view examples are separate contracts. Do not mix their methods or credentials with this API.

For the current JavaScript artifact, collection routes are rooted at:

```text
/v1/projects/{project}/databases/{database}/collections/{collection}
```

`POST /documents` creates a server-generated document ID. `GET`, `PUT`, `PATCH` and `DELETE /documents/by-key?id={id}` address a document. `POST /query` returns one cursor page. Use the SDK's named operations to encode identifiers and validate responses.

Configure exactly one HTTPS endpoint or resolver. A configured endpoint proves only that lane. Neither an API origin nor a published SDK establishes region failover, production readiness or automatic discovery.

## Credentials, retries and compatibility

The current SDKs carry bearer credentials. Authorization still depends on the serving API and the selected project/database. Historical API-key compatibility is not proof of deployed sign-in or JWT authorization. See [authentication](/de/guides/authentication).

JavaScript and C# send each request once. They do not provide mutation deduplication; `Idempotency-Key` is unsupported in this contract. Go has bounded retries for safe operations. Preserve versions and reconcile uncertain writes rather than blindly replaying them. See [errors](/de/guides/errors).

The retained C# starter has an isolated, package-only real-binary CRUD/query qualification receipt. It covers persisted values, preconditions, authorization denial and cleanup for that exact candidate. It does not qualify the latest source revision, other SDKs, public package releases, multi-region behavior or the hosted customer journey.

</div>
