# Platform databases (dogfooding)

TerraScale's own services keep their data in TerraBase like any customer. TerraBase seeds their databases itself, on
every start, with constants in code (`src/TerraBase/Features/Platform/PlatformDatabases.cs`):

| What | Id | Name |
|---|---|---|
| Organization | `00000000000000000000000001` | TerraScale (enterprise plan) |
| ts-auth project | `00000000000000000000000002` | `ts-auth` |
| ts-auth database (project02) | `00000000000000000000000003` | `ts-auth` |
| Dashboard project | `00000000000000000000000005` | `ts-dashboard` |
| Dashboard database (project05) | `00000000000000000000000004` | `ts-dashboard` |

Each project owns exactly one database. The public constants are `OrganizationId`, `AuthProjectId`, `AuthId`, `Auth`,
`DashboardProjectId`, `DashboardId` and `Dashboard` on `PlatformDatabases`. Each fixed project has a version-1 catalog binding to `https://auth.terrascale.tech/terrascale`, with both the
storage organization and authority organization equal to org01. Missing bindings are seeded locally, with fixed
verification timestamps; conflicting issuer/organization/version/authority records are refused and never overwritten.
No call to auth is required to create a binding. Auth owns authorization to issue matching project tokens.

Auth and dashboard use these fixed values
through ts-dotnet; there is no configuration or legacy-name fallback. This is a greenfield storage contract: the old
`platform-auth`/`platform-dashboard` names are removed, with no migration.

TerraBase seeds the storage catalog without calling ts-auth. Auth separately seeds the matching TerraScale identity
organization and its two projects at startup; these identity records belong to auth. Service collections and signing
keys also belong to their owning service.

Both databases are placed in every region ([all-regions placement](all-regions-placement.md)): each region that serves,
now or later, holds a full copy. Their records name no home region, so every region seeds the same record and manages
them as its own. A record seeded with a fixed region list takes this placement on the next seed.

## Seeding

- A background service (`PlatformSeed`) creates whatever is missing once the catalog gate is open, retrying every 5
  seconds until the catalog takes the writes. TerraBase serves before and while it runs; nothing waits for it. Before the
  gate opens it writes nothing: an unverified catalog may be a restored copy, and a seeded one is no longer a new
  cluster's, so the first machine's `catalog-bootstrap` would be refused.
- Every write is conditional on absence under the fixed ids and fixed timestamps, so restarts, concurrent runs on one
  node and independent nodes or regions all converge on identical records. An id or name that holds something else is
  never overwritten: the seed logs an error and stops.
- Every machine seeds its local catalog. The system catalog and placed databases use the existing Anna replication
  path ([multi-region-replication.md](multi-region-replication.md) section 15). A three-region placement is not proof
  of live convergence: the rebuild must verify data replication after deploying the new image.
- Collections belong to the service that owns the data: ts-auth creates its collections at startup (idempotent), and
  the dashboard creates its own. TerraBase does not keep a copy of either list.

## Who reaches them

The required platform issuer is `https://auth.terrascale.tech/terrascale` and its
audience includes `https://api.terrascale.tech`. Missing production auth settings
default to this pair. Before cutover, verify that the exact deployed revision
rejects incompatible issuer/audience settings and preserves the required trust.
Defaults alone do not establish validation. Missing settings must never disable
platform authentication.

- The platform operator: TerraBase's internal listener (the verified private internal origin, HTTP/2
  prior knowledge, sending `X-TerraBase-Peer-Credential` from `TERRABASE_PEER_SECRET`). Verification outcomes are recorded; refusal is the following rollout phase. ts-auth stores here
  (`App.StorageProject`, `App.StorageDatabase`).
- Matching scoped project tokens signed by the global issuer: only their own project and database routes, with the
  required scopes. Another project, issuer, customer API key or ordinary user membership cannot grant access
  (`PlatformDatabasesTests`). The platform storage organization has no catalog members or API keys.
- Usage of the platform organization is metered under its own id and never under a customer organization.

## How the dashboard uses its database

The dashboard (C# rewrite) reaches TerraBase through the ts-dotnet SDK, like ts-auth.
In this example, `internalOrigin` is the verified private TerraBase origin supplied by
the current hosting integration, not a hardcoded discovery hostname:

```csharp
var http = new HttpClient(TerraBaseHttp.CreateHandler());
http.DefaultRequestHeaders.Add("X-TerraBase-Peer-Credential",
    Environment.GetEnvironmentVariable("TERRABASE_PEER_SECRET")
        ?? throw new InvalidOperationException("TERRABASE_PEER_SECRET is required"));
var database = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
{
    // This internal-only transport sends the peer credential; the SDK sends HTTP/2 prior knowledge.
    BaseAddress = internalOrigin,
}).Database("00000000000000000000000005", "ts-dashboard");

// At startup, create the collections it needs; an existing name answers 409 name_taken, which is fine.
await database.CreateCollectionAsync("plans");
```

Every route the SDK calls is listed in ts-dotnet's `contracts/terrabase/routes.json`; the monorepo's
`TerraBaseSdkContractTests` fails when TerraBase stops serving one.

## Explore API (platform admins)

Read-only, on the public listener, for the dashboard's platform-admin pages. The dashboard forwards the signed-in user's
ts-auth access token; TerraBase accepts it only when it is a user token (no `project_id`) whose `platform_role` claim is
`admin` (ts-auth issues that claim to platform admins). Anyone else: 401 without a token, 403 with one. Only
`ts-auth` and `ts-dashboard` resolve; any other database answers 404.

| Route | Answer |
|---|---|
| `GET /v1/platform/databases` | `{items: [{name, id, homeRegion, replicaRegions, allRegions}]}` |
| `GET /v1/platform/databases/{database}/collections` | Up to 200 collections in name order |
| `GET /v1/platform/databases/{database}/collections/{collection}/documents?limit=&cursor=` | A page of documents |
| `GET /v1/platform/databases/{database}/collections/{collection}/documents/by-key?id=` | One document |

- **Redaction.** Before a document leaves, every field whose name (lowercased, without `_` and `-`) contains `secret`,
  `password`, `hash`, `digest`, `sealed`, `box`, `private`, `token`, `mac`, `cipher`, `nonce`, `salt`, `otp`,
  `recovery`, `credential` or `masterkey`, or is exactly `d`, `p`, `q`, `dp`, `dq`, `qi`, `k`, `key`, `code`, `codes` or
  `pin`, reads `"[redacted]"`, at any depth. ts-auth's sealed signing keys (`secretBox`), private key parameters and
  password hashes never leave. Over-redaction is the intended failure.
- **Audit.** Every read logs `Platform admin <user id> read <database>/<collection>/<document id>` (never the content).
  Durable audit storage is separate work.
- **No writes.** There is no write route; changes go through the owning service.

The ts-dotnet SDK client for these routes ships with the dashboard work (TerraScale/ts-dotnet); its routes join
`contracts/terrabase/routes.json` and the monorepo's `TerraBaseSdkContractTests` then checks them too.
