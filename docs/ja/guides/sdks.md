---
title: SDK compatibility and qualification
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

# SDK compatibility and qualification

Use a candidate SDK matched to the project-scoped C# TerraBase Documents API:
`/v1/projects/{project}/databases/{database}/…`. Start with the
[C# quickstart](/ja/getting-started/csharp). Candidate source, a locally packaged
SDK, a stub run and real-binary compatibility evidence establish different
things; they do not establish public release availability or cross-SDK parity.

## Candidate client surfaces

| SDK | Project-scoped surface | Qualification boundary |
| --- | --- | --- |
| C# `TerraScale.DotNet` | `TerraBaseDocumentsClient`, `Database(projectId, databaseName)`, document CRUD, `QueryAsync`, `TransactAsync` | Exact locally packaged candidate passed real-binary CRUD, query, precondition, denial and cleanup checks; public NuGet installation and deployed JWT journey remain unqualified |
| JavaScript `@terrascale/sdk` candidate | `createTerraBaseV1Client`, `database(name)`, collection CRUD, typed filters, cursor pages, transactions and shapes | Candidate package publication and real-runtime/customer journey remain unqualified |
| Go `terrascale.tech/sdk/v1` candidate | `New(Config)`, `Database(projectId, name)`, collections, document CRUD, typed queries and transactions | Pin an immutable module version; source/test helpers do not establish the deployed journey or parity with C# |
| Legacy Next.js and snippet fixtures | `createTerraScaleClient`, typed SQL and `TerraScaleSyncV1Client`, pinned to `@terrascale/sdk` 0.3.0 | Local stub references only; that pin is not publicly installable and targets a different contract |

Package availability was checked on October 7, 2026. The current candidates
are not published on public NuGet/npm. The latest public `@terrascale/sdk` version
on npm is `0.0.2`; its presence does not make the legacy `0.3.0` fixture or the current
candidate installable. Do not silently replace an exact candidate artifact
with another registry version.

The C# `NativeClient`/`NativeDiscoveryRouter` and the retired UUID/shard
protocol are legacy. New examples use project/database names, named
collections and JSON documents. A document identifier may still be a UUID;
that does not turn the request into the retired tenant/shard protocol.
C# query-v1 uses typed filters, sorting, projection and cursor pages; the
legacy SQL/reactive API is not its query contract.

## Endpoints and safe writes

Configure exactly one explicit HTTPS endpoint or caller-supplied resolver:
C# `BaseAddress`/`EndpointResolver`, JavaScript `baseUrl`/`endpointResolver`,
or Go `BaseURL`/`Resolver`. Automatic Atlas integration is deferred; these
options do not promise pin verification, regional fallback or proxy fallback.
See [Atlas discovery](/ja/guides/atlas) for the candidate public contract.

C# clients use a caller-owned `HttpClient`; use
`TerraBaseHttp.CreateHandler()` to disable automatic redirects and cookies.
The caller disposes the transport. Keep credentials out of source control,
logs and browser bundles.

Document versions are opaque ETags. C# supports create-only
`TerraBasePrecondition.Absent` and version-matched
`TerraBasePrecondition.Matches(version)` writes. Pass returned `NextCursor`
values unchanged when paging. The candidate supports database-scoped atomic
transactions; qualification must cover the operations the application uses.

C# sends requests once. The producer does not promise mutation deduplication
and rejects `Idempotency-Key`; a timeout or lost response is not proof of an
abort. Do not replay an uncertain mutation automatically. Go has its own
bounded retry policy for safe operations; do not assume every SDK shares it.

## Credentials and service ownership

The candidate C# Documents client accepts historical `tb_` API keys with
`documents:read` and/or `documents:write` permissions. This is isolated
compatibility evidence. The product design accepts ts-auth JWTs; deployed
JWT validation, authorization and sign-in remain separate gates. A
historical-key CRUD result cannot qualify that production journey.

Auth owns users, organizations, projects and identity lifecycle. Store all
application data in TerraBase through its SDK. Do not build duplicate identity
or project management models from retained SDK management methods. Dashboard
sign-in stays on native dashboard pages.

## Qualification gates

1. **Package:** verify the exact artifact version and SHA-256; compile a fresh
   consumer against the package, without a source project reference. A local
   package is not a public NuGet/npm release.
2. **Real producer:** run the same CRUD example against the independently
   identified C# release binary using the SDK Testing helper and an isolated
   allocation. Check persisted values, version preconditions and cleanup.
   Missing inputs or a stub cannot pass this gate.
3. **Discovery and authorization:** qualify deployed Atlas resolution, pin
   verification, direct regional dispatch and accepted ts-auth JWTs. A
   configured endpoint with historical keys establishes only compatibility
   for that lane.
4. **Customer journey:** qualify supported provisioning, native dashboard
   sign-in, application authorization and any live-update operations through
   public SDKs/APIs. Check each SDK independently.

`npm run qualify:dotnet` is the exact-package C# lane; its current evidence and
commands are described in the [C# quickstart](/ja/getting-started/csharp). The
[sanitized receipt](https://git.terrascale.tech/TerraScale/ts-docs/src/branch/main/examples/terrabase-dotnet/qualification.json)
records `PASS_REAL_TERRABASE_STARTER` for SDK source revision
`13f795b661654b0d50453acf71b70a681d0458c0` against producer revision
`ff256067015433c8c40b59879f1282d2a40480ce`. It verifies CRUD, a matching query,
stale-version rejection, read-only write denial, invalid-credential rejection
and cleanup. This is an exact locally packaged candidate, not a published release. A passing isolated CRUD receipt still leaves public package
availability, Atlas, product JWTs, other SDK runtimes and the deployed customer
journey unqualified.

`npm test` checks the legacy deterministic stub and documentation consistency.
`npm run test:remote` is the explicit credential-gated legacy snippet lane;
it does not import the new candidate C# package or prove the candidate
Documents API. The [legacy Next.js guide](/ja/getting-started/) and
[snippets](/ja/snippets) retain their version pins as historical references.
