---
title: SDK reference
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# SDK reference

Use a TerraBase SDK to store and query JSON documents in a project database. Each SDK calls the project-scoped Documents API at `https://api.terrascale.tech`.

## Languages and installation

| Language | Package / entrypoint | Runtime | Install |
| --- | --- | --- | --- |
| JavaScript / TypeScript | `@terrascale/sdk@0.0.5`, `createTerraBaseV1Client` | Node 22 or 24; npm ≥11; bundled TypeScript declarations | npm; [quickstart](/hi/getting-started/javascript) |
| C# | `TerraScale.DotNet`, `TerraBaseDocumentsClient` | .NET 10; source also targets .NET 11 with that toolchain | Build the package from source; [quickstart](/hi/getting-started/csharp) |
| Go | `terrascale.tech/sdk/v1`, `New(Config)` | Go ≥1.25 | `go get terrascale.tech/sdk@v0.0.2` |
| Python | `terrascale`, `TerraScale` / `AsyncTerraScale` | Python ≥3.10, httpx | `pip install` from Git; [quickstart](/hi/getting-started/python) |
| Rust | `terrascale::v1`, `Client::for_project` | Rust 2021 edition, Tokio | Git dependency; not on crates.io yet |

The npm package declares the latest major Chrome, Firefox, Safari and Edge. Its React, Svelte and TanStack entrypoints have their own peer package requirements. There is no Java SDK yet; call the HTTP API directly from those languages.

### JavaScript / TypeScript

```bash
npm install --save-exact @terrascale/sdk@0.0.5
```

Import from `@terrascale/sdk`. The root exposes `createTerraBaseV1Client`, `v1Filter` and typed errors. `client.database(name).collection(name)` selects a collection. It provides `create`, `get`, `put`, `patch`, `delete`, `query`, `pages` and `items`. See [the complete example](/hi/getting-started/javascript).

### C#

`TerraScale.DotNet` is not on nuget.org yet. Build version `0.0.4` from the public [ts-dotnet](https://github.com/TerraScale/ts-dotnet) source into a local package feed; the [C# quickstart](/hi/getting-started/csharp) shows the commands.

`Database(projectId, databaseName)` returns a handle with `CreateDocumentAsync`, `GetDocumentAsync`, `PutDocumentAsync`, `PatchDocumentAsync`, `DeleteDocumentAsync`, `QueryAsync` and `TransactAsync`. A caller-owned `HttpClient` must disable redirects and cookies; use `TerraBaseHttp.CreateHandler()` and dispose the transport yourself.

### Go

```bash
go get terrascale.tech/sdk@v0.0.2
```

Import `terrascale.tech/sdk/v1`. `New(Config)` takes a `BaseURL` (or a `Resolver`) and a `Token`. `Database(projectId, name)` and named collections scope document operations.

### Python

```bash
pip install "terrascale @ git+https://github.com/TerraScale/ts-python@v0.0.1"
```

`TerraScale()` takes the API key and project ID (or reads `TERRASCALE_API_KEY` and `TERRASCALE_PROJECT_ID`), exchanges the key for access tokens and refreshes them. `ts.database(name).collection(name)` provides `create`, `get`, `put`, `patch`, `delete`, `list`, `query`, `pages`, `items`, `search` (vector) and the TTL methods; the database handle runs SQL and transactions. `AsyncTerraScale` is the asyncio twin. See the [Python quickstart](/hi/getting-started/python).

### Rust

The `terrascale` crate is not on crates.io yet. Add it from the public [ts-rust](https://github.com/TerraScale/ts-rust) repository:

```bash
cargo add terrascale --git https://github.com/TerraScale/ts-rust
cargo add tokio -F tokio/full
cargo add serde_json
```

Use `terrascale::v1`. `Client::for_project(projectId, accessToken).build()?` sends requests to `https://api.terrascale.tech`. `client.database(name).collection(name)` provides `get`, `put`, `create`, `patch`, `delete`, `query` and `pages`; `database.run_sql` runs the read-only SQL subset. The token is the access token from the [key exchange](/hi/guides/authentication).

## Contract and routing

The current public contract uses project IDs, database and collection names, JSON documents, opaque versions, JSON filter queries and database-scoped transactions. Older SQL, UUID/shard and reactive-view examples are separate contracts. Do not mix their methods or credentials with this API.

For the current JavaScript artifact, collection routes are rooted at:

```text
/v1/projects/{project}/databases/{database}/collections/{collection}
```

`POST /documents` creates a server-generated document ID. `GET`, `PUT`, `PATCH` and `DELETE /documents/by-key?id={id}` address a document. Document ids are canonical ULIDs: 26 uppercase Crockford base32 characters. Any other id answers `400 invalid_document_id`. Omit the id on create and TerraBase generates one. `POST /query` returns one cursor page. Use the SDK's named operations to encode identifiers and validate responses.

Configure exactly one HTTPS endpoint or resolver, normally `https://api.terrascale.tech`. The SDKs do not discover endpoints or fail over between regions on their own.

## Credentials, retries and compatibility

Each SDK sends an access token as `Authorization: Bearer`. Exchange your dashboard API key for that token first; the key's project, database and permissions decide what the token can do. See [authentication](/hi/guides/authentication).

JavaScript and C# send each request once. They do not provide mutation deduplication; `Idempotency-Key` is unsupported in this contract. Go has bounded retries for safe operations. Preserve versions and reconcile uncertain writes rather than blindly replaying them. See [errors](/hi/guides/errors).

</div>
