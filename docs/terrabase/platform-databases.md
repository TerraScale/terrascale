# Platform databases (dogfooding)

TerraScale's own services keep their data in TerraBase like any customer. TerraBase seeds their databases itself, on
every start, with constants in code (`src/TerraBase/Features/Platform/PlatformDatabases.cs`):

| What | Id | Name |
|---|---|---|
| Organization | `00000000000000000000000001` | TerraScale platform (enterprise plan) |
| Project | `00000000000000000000000002` | platform |
| ts-auth's database | `00000000000000000000000003` | `platform-auth` |
| The dashboard's database | `00000000000000000000000004` | `platform-dashboard` |

Both databases are placed in gru (home), sjc and fra. Off Fly (development, tests) they are placed in the node's own
region only.

## Seeding

- A background service (`PlatformSeed`) creates whatever is missing, retrying every 5 seconds until the catalog takes
  the writes. TerraBase serves before and while it runs; nothing waits for it.
- Every write is conditional on absence under the fixed ids and fixed timestamps, so restarts, concurrent runs on one
  node and independent nodes or regions all converge on identical records. An id or name that holds something else is
  never overwritten: the seed logs an error and stops.
- Today a catalog follower (a region other than `PRIMARY_REGION`) seeds nothing: it replicates its primary's catalog.
  Once the catalog is leaderless (replication slice R10a) every machine seeds, with the same result.
- Collections belong to the service that owns the data: ts-auth creates its collections at startup (idempotent), and
  the dashboard creates its own. TerraBase does not keep a copy of either list.

## Who reaches them

- The platform operator: TerraBase's internal listener (`http://top1.nearest.of.ts-terrabase.internal:8081`, HTTP/2
  prior knowledge, no credential). Fly's private network is the trust boundary. ts-auth stores here
  (`App.StorageProject`, `App.StorageDatabase`).
- Nobody else. The platform organization has no members, no API keys and no ts-auth project binding, so customer API
  keys, ts-auth user tokens and project tokens all get 404 on its routes (`PlatformDatabasesTests`).
- Usage of the platform organization is metered under its own id and never under a customer organization.

## How the dashboard uses its database

The dashboard (C# rewrite) reaches TerraBase through the ts-dotnet SDK, like ts-auth:

```csharp
var http = new HttpClient(TerraBaseHttp.CreateHandler());
var database = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
{
    // No credential: the internal listener trusts Fly's private network. The SDK sends HTTP/2 prior knowledge.
    BaseAddress = new Uri("http://top1.nearest.of.ts-terrabase.internal:8081"),
}).Database("00000000000000000000000002", "platform-dashboard");

// At startup, create the collections it needs; an existing name answers 409 name_taken, which is fine.
await database.CreateCollectionAsync("plans");
```

Every route the SDK calls is listed in ts-dotnet's `contracts/terrabase/routes.json`; the monorepo's
`TerraBaseSdkContractTests` fails when TerraBase stops serving one.
