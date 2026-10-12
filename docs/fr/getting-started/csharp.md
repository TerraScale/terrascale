---
title: C# quickstart
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# C# quickstart

Run the console example through `TerraScale.DotNet.TerraBase.TerraBaseDocumentsClient`. It creates, reads, updates, queries and deletes one synthetic document, verifies stale-version rejection and uses only the SDK for database requests.

::: warning Candidate package required
The public NuGet package endpoint returns 404 as of October 12, 2026 (UTC). Version `0.0.2` remains an in-progress candidate, not a verified public release. Obtain an exact verified candidate package containing `TerraBaseDocumentsClient`. Source version numbers do not establish public installation or production JWT authorization. See [SDK reference](/fr/guides/sdks).
:::

## 1. Obtain the example and package

Install .NET SDK 10 or newer with the .NET 10 runtime. Download <a href="/examples/terrabase-dotnet/Program.cs" download>Program.cs</a> and <a href="/examples/terrabase-dotnet/TerraBaseStarter.csproj" download>TerraBaseStarter.csproj</a> into one directory. Obtain the verified package's exact version, immutable bytes and SHA-256 through your supported access process.

The project uses a package reference, with no SDK source dependency. A legacy `NativeClient` package is not interchangeable with the project-scoped Documents API.

## 2. Supply authorized connection details

Set `TERRASCALE_API_URL`, `TERRASCALE_PROJECT_ID`, `TERRASCALE_DATABASE_NAME` and `TERRASCALE_COLLECTION_NAME` from your isolated development allocation. Supply `TERRASCALE_ACCESS_TOKEN` privately through the application's secret manager or shell. The console app reads environment variables; it does not load dotenv files.

Use the actual provisioned HTTPS endpoint and a credential the serving API accepts. The project, database and collection must exist with document read/write access. The example does not create account identity resources or implement an unverified credential exchange.

## 3. Build and run

From the example directory:

```bash
dotnet build -c Release -p:TerraBaseSdkPackageVersion=EXACT_VERSION \
  --source /path/to/verified-sdk-feed --source https://api.nuget.org/v3/index.json
dotnet bin/Release/net10.0/TerraBaseStarter.dll
```

Replace `EXACT_VERSION` with the verified package version and the feed path with its local location. Success prints `PASS_TERRABASE_CRUD_QUERY`. The example uses 60-second overall and 15-second request deadlines, disables cookies and redirects, and prints no credentials or customer document bodies.

## 4. Preserve versions and bound queries

<<< @/../examples/terrabase-dotnet/Program.cs{31-78}

Versions are opaque. Use `TerraBasePrecondition.Matches(version)` for dependent updates and deletes; a stale version throws `TerraBasePreconditionFailedException`. `TerraBaseQuery` uses JSON filters and carries `NextCursor` into the next request's `Cursor` with unchanged filter, sort and projection. See [queries and pagination](/fr/guides/queries).

Only the generated document is deleted. An interrupted request can leave it behind; reconcile its outcome before retrying. The SDK sends each request once and this contract does not support mutation deduplication or `Idempotency-Key`. See [errors](/fr/guides/errors).

## Qualification boundary

This repository provides the starter source without a retained real-service qualification receipt. Validate persisted CRUD/query values, version preconditions, read-only write denial, invalid credentials and cleanup against the exact candidate package in an authorized isolated real-service run.

Public NuGet availability, production JWT authorization, automatic endpoint resolution, dashboard sign-in and multi-region behavior require separate current evidence. The example source alone does not establish them.

</div>
