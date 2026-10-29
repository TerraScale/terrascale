# TerraBase consumers

ts-auth (in the monorepo, TerraScale/terrascale) persists all its state through the TerraBase Documents client
from this repository, consumed as source (`$(TsDotnetRoot)/src/TerraScale.DotNet`), not a package. The dashboard's
C# rewrite will use the same clients.

## How the monorepo pins this SDK

- `.github/workflows/ci.yml` pins `TS_DOTNET_REV` to a full commit SHA of this repository and checks it out next to
  the monorepo (deploy key `TS_DOTNET_DEPLOY_KEY`); the Dockerfile takes it as the `ts-dotnet` build context.
- Locally, clone ts-dotnet next to the monorepo or set `TsDotnetRoot`.

## Adopting a new revision

1. Merge the ts-dotnet change to `main`.
2. Set `TS_DOTNET_REV` in the monorepo's `ci.yml` to that merge commit.
3. The monorepo's ts-auth suites run against the TerraBase built from the same monorepo commit, and
   `TerraBaseSdkContractTests` checks that TerraBase serves every route in `contracts/terrabase/routes.json`.

## Testing

No TerraBase runs in this repository. Change a route in the clients and in `contracts/terrabase/routes.json`
together: `TerraBaseContractTests` fails when they disagree, and the monorepo fails when TerraBase does not serve
the manifest.

## Store usage

The [zero-configuration operating contract](../AGENTS.md#zero-configuration-user-decision-2026-10-09-do-not-revert)
requires automatic first-party endpoint discovery and production credential
creation/delivery. The SDK still needs a caller-supplied endpoint/resolver
pending TS-507; the example shows that existing API boundary, not manual
production onboarding or a shipped automatic acquisition API. The scoped
consumer token and project selection remain authorization requirements.
The ts-auth adoption and persistence gaps above remain blockers.

Configure the supplied `HttpClient` with `AllowAutoRedirect = false` so a
redirect cannot forward write bodies or management identity headers to another
origin. The SDK does not reconfigure or dispose the supplied client.

```csharp
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

using var httpClient = new HttpClient(TerraBaseHttp.CreateHandler());
var client = new TerraBaseDocumentsClient(httpClient, new TerraBaseDocumentsClientOptions
{
    BaseAddress = terraBaseOrigin,
    ApiKey = terraBaseApiKey
});
TerraBaseDatabase database = client.Database(projectId, "auth");

// Create only: fails with TerraBasePreconditionFailedException when the id exists.
TerraBaseWriteResult created = await database.PutDocumentAsync(
    "users", user.Id, JsonSerializer.SerializeToElement(user), TerraBasePrecondition.Absent, cancellationToken);

// Update only if nobody changed it since it was read.
TerraBaseDocument? current = await database.GetDocumentAsync("users", user.Id, cancellationToken);
if (current is not null)
{
    await database.PutDocumentAsync(
        "users", user.Id, JsonSerializer.SerializeToElement(updated),
        TerraBasePrecondition.Matches(current.Version), cancellationToken);
}
```

A `TerraBasePreconditionFailedException` means another writer won; re-read the
document before trying again. The client never retries on its own.
