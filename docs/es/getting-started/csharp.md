---
title: C# quickstart
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# C# quickstart

Run the console example through `TerraScale.DotNet.TerraBase.TerraBaseDocumentsClient`. It creates, reads, updates, queries and deletes one synthetic document, verifies stale-version rejection and uses only the SDK for database requests.

::: warning Candidate package required
The public NuGet package endpoint returns 404 as of October 23, 2026 (UTC). Version `0.0.3` remains an in-progress candidate, not a verified public release. Obtain an exact verified candidate package containing `TerraBaseDocumentsClient`. Source version numbers do not establish public installation or production JWT authorization. See [SDK reference](/es/guides/sdks).
:::

## 1. Obtain the example and package

Install .NET SDK 10 or newer with the .NET 10 runtime. Download <a href="/examples/terrabase-dotnet/Program.cs" download>Program.cs</a> and <a href="/examples/terrabase-dotnet/TerraBaseStarter.csproj" download>TerraBaseStarter.csproj</a> into one directory. Obtain the verified package's exact version, immutable bytes and SHA-256 through your supported access process.

The project uses a package reference, with no SDK source dependency. A legacy `NativeClient` package is not interchangeable with the project-scoped Documents API.

## 2. Supply connection details

Create a database and an API key in the dashboard as described in [Getting started](/es/getting-started/#create-an-account-a-database-and-an-api-key). Exchange the key for a 15-minute access token, then set the project ID (dashboard **Organization** page, **Current project**), the database name and a collection name. The app creates the collection when it does not exist yet. It reads environment variables; it does not load dotenv files.

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY  # paste the key, then press Enter
export TERRASCALE_ACCESS_TOKEN="$(curl -sS --fail-with-body \
  -u "${TERRASCALE_API_KEY%%_*}:$TERRASCALE_API_KEY" -d grant_type=client_credentials \
  https://auth.terrascale.tech/terrascale/oauth/token \
  | sed -nE 's/.*"access_token": *"([^"]+)".*/\1/p')"
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
export TERRASCALE_COLLECTION_NAME='posts'
```

The example passes the access token as `ApiKey`; the SDK sends it as `Authorization: Bearer`. The dashboard key itself answers `401` as a bearer token. Run the exchange again when the token expires. See [authentication](/es/guides/authentication).

## 3. Build and run

From the example directory:

```bash
dotnet build -c Release -p:TerraBaseSdkPackageVersion=EXACT_VERSION \
  --source /path/to/verified-sdk-feed --source https://api.nuget.org/v3/index.json
dotnet bin/Release/net10.0/TerraBaseStarter.dll
```

Replace `EXACT_VERSION` with the verified package version and the feed path with its local location. Success prints `PASS_TERRABASE_CRUD_QUERY`. The example uses 60-second overall and 15-second request deadlines, disables cookies and redirects, and prints no credentials or customer document bodies.

## 4. Preserve versions and bound queries

<<< @/../examples/terrabase-dotnet/Program.cs{31-88}

Versions are opaque. Use `TerraBasePrecondition.Matches(version)` for dependent updates and deletes; a stale version throws `TerraBasePreconditionFailedException`. `TerraBaseQuery` uses JSON filters and carries `NextCursor` into the next request's `Cursor` with unchanged filter, sort and projection. See [queries and pagination](/es/guides/queries).

Only the generated document is deleted. An interrupted request can leave it behind; reconcile its outcome before retrying. The SDK sends each request once and this contract does not support mutation deduplication or `Idempotency-Key`. See [errors](/es/guides/errors).

## Qualification boundary

This repository provides the starter source without a retained real-service qualification receipt. Validate persisted CRUD/query values, version preconditions, read-only write denial, invalid credentials and cleanup against the exact candidate package in an authorized isolated real-service run.

Public NuGet availability, automatic endpoint resolution and multi-region behavior require separate current evidence. The example source alone does not establish them.

</div>
