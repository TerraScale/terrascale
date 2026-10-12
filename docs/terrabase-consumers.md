# TerraBase consumers

ts-auth's store (`src/TerraScale.Auth.Store` in TerraScale/ts-auth) is meant to
persist through the TerraBase client from this repository (TS-489), consumed as
a source dependency, not a package. Today it references the SDK project but
does not call the TerraBase client yet.

The current consumption details below were checked against ts-auth revision
`3c00fa7e85cde1f72626f5fe624b9144d7804f8d`.

## How ts-auth pins this SDK

- ts-auth CI pins `TS_DOTNET_REV` and `TS_LIBS_REV` to full 40-hex commit SHAs
  in both `.forgejo/workflows/ci.yml` and `.forgejo/workflows/slow.yml`.
  Both currently pin ts-dotnet to `20862d391a8c0e746af010b289a1a6dc7ae7867b`
  and ts-libs to `55b260872f8d4fb1cfa0975576632b4e022e4c91`.
- `scripts/ci/checkout-dotnet-deps.sh` fetches those exact revisions with the
  `ORG_READ_TOKEN` org secret into a job-owned directory and sets
  `TerraScaleRoot`.
- `TerraScale.Auth.Store.csproj` references the SDK project. Neither
  `TerraScale.Auth.Fast.csproj` nor `TerraScale.Auth.Slow.csproj` currently
  references the test helper; add the second reference when adopting it:

```xml
<ProjectReference Include="$(TsDotnetRoot)/src/TerraScale.DotNet/TerraScale.DotNet.csproj"
                  SetTargetFramework="TargetFramework=net10.0" />
<!-- proposed addition in each test project -->
<ProjectReference Include="$(TsDotnetRoot)/src/TerraScale.DotNet.Testing/TerraScale.DotNet.Testing.csproj"
                  SetTargetFramework="TargetFramework=net10.0" />
```

## Adopting a new revision

1. Merge the ts-dotnet change to `main`.
2. Set `TS_DOTNET_REV` in both ts-auth workflows (`ci.yml` and `slow.yml`) to
   the full 40-hex SHA of that merge commit, so they never test different
   revisions.
3. ts-auth's tests run against the TerraBase built from the same monorepo commit
   (`TERRABASE_BINARY`), so an SDK/TerraBase contract mismatch fails there.
4. Locally, clone ts-dotnet next to ts-auth, or pass
   `-p:TerraScaleRoot=<dir containing ts-dotnet and ts-libs>` to `dotnet build`.

When TerraBase's contract changes, update the client in this repository, then
`TS_DOTNET_REV` in the monorepo.

## Testing against the real TerraBase

This SDK's TerraBase tests run a real TerraBase binary (`TERRABASE_BINARY`),
never a fake server. ts-auth has not adopted this harness yet: its store only exposes a
readiness probe that currently fails closed (`UnavailableStorageHealth`), and
its tests use `FakeStorageHealth` for readiness behavior. Updating the SDK pin
alone does not implement persistence or verify ts-auth against TerraBase.
`TerraBaseTestServer` is test-framework neutral; share one per test session:

```csharp
using TerraScale.DotNet.Testing;
using TerraScale.DotNet.TerraBase;

var (server, unavailable) = await TerraBaseTestServer.StartAsync(cancellationToken: cancellationToken);
if (server is null)
{
    // Skip locally; fail when CI=true. unavailable says why (TERRABASE_BINARY unset).
}

await using (server)
{
    TerraBaseTestTenant tenant = await server.CreateTenantAsync(cancellationToken);
    TerraBaseDatabase database = server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth");
    await database.CreateAsync(cancellationToken);
    // server.Management() acts with the generated service credential on the admin listener;
    // server.Http trusts only the server's generated certificate.
}
```

If startup and cleanup both fail, the original exception keeps its type and
contains the server in `Data[nameof(TerraBaseTestServer)]`. Retain that owner and
retry `DisposeAsync` after resolving the cleanup problem. The secondary error is
available in `Data["TerraBaseCleanupException"]`.

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
