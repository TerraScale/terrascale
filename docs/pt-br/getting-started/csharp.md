---
title: C# quickstart
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# C# quickstart

Run the console example through `TerraScale.DotNet.TerraBase.TerraBaseDocumentsClient`. It creates, reads, updates, queries and deletes one synthetic document, verifies stale-version rejection and uses only the SDK for database requests.

::: info Package from source
`TerraScale.DotNet` is not on nuget.org yet. Step 1 builds version `0.0.12` from the pinned source revision into a local feed; no publication is required.
:::

## 1. Obtain and build the example

Install .NET SDK 10 or newer with the .NET 10 runtime. Download <a href="/examples/terrabase-dotnet/Program.cs" download>Program.cs</a> and <a href="/examples/terrabase-dotnet/TerraBaseStarter.csproj" download>TerraBaseStarter.csproj</a> into one directory. From that directory, build the SDK package into a `feed` directory beside it, then build the example. Continue only when every command succeeds:

```bash
git clone --no-checkout https://github.com/TerraScale/ts-dotnet.git ../ts-dotnet &&
git -C ../ts-dotnet checkout --detach e786f2e38b34985293446fd1eb5bfa602e041b85 &&
dotnet pack ../ts-dotnet/src/TerraScale.DotNet -c Release -o ../feed &&
dotnet build -c Release -p:TerraBaseSdkPackageVersion=0.0.12 \
  -p:RestorePackagesPath=../packages-e786f2e \
  --source ../feed --source https://api.nuget.org/v3/index.json
```

The project uses a package reference, with no SDK source dependency. The revision-specific package cache prevents an older local pack with the same version from being reused. A legacy `NativeClient` package is not interchangeable with the project-scoped Documents API.

## 2. Supply connection details

Create a database and an API key in the dashboard as described in [Getting started](/pt-br/getting-started/#create-an-account-a-database-and-an-api-key). Set the project ID (dashboard **Organization** page, **Current project**), the database name and a collection name. The app creates the collection when it does not exist yet. It reads environment variables; it does not load dotenv files.

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
export TERRASCALE_COLLECTION_NAME='posts'
```

The example passes the access token as `ApiKey`; the SDK sends it as `Authorization: Bearer`. The dashboard key itself answers `401` as a bearer token. Run the exchange again when the token expires. See [authentication](/pt-br/guides/authentication).

## 3. Exchange the key and run

Use Bash, `curl` and `jq` in the example directory. With the build complete, exchange the key for a 60-second token and immediately run the built application. The key goes to curl through stdin, and a failed read, HTTP request or token extraction prevents launch:

```bash
set +x
set -o pipefail
read -rs TERRASCALE_API_KEY &&  # paste the key, then press Enter
TERRASCALE_ACCESS_TOKEN="$(
  printf 'user = "%s:%s"\n' "${TERRASCALE_API_KEY%%_*}" "$TERRASCALE_API_KEY" |
    curl -sS --fail-with-body --config - -d grant_type=client_credentials \
      https://auth.terrascale.tech/terrascale/oauth/token |
    jq -er '.access_token | select(type == "string" and length > 0)'
)" &&
export TERRASCALE_ACCESS_TOKEN &&
dotnet bin/Release/net10.0/TerraBaseStarter.dll
```

Adjust `../feed` if you put the feed elsewhere. Success prints `PASS_TERRABASE_CRUD_QUERY`. The example uses 60-second overall and 15-second request deadlines, disables cookies and redirects, and prints no credentials or customer document bodies.

## 4. Preserve versions and bound queries

<<< @/../examples/terrabase-dotnet/Program.cs{31-88}

Versions are opaque. Use `TerraBasePrecondition.Matches(version)` for dependent updates and deletes; a stale version throws `TerraBasePreconditionFailedException`. `TerraBaseQuery` uses JSON filters and carries `NextCursor` into the next request's `Cursor` with unchanged filter, sort and projection. See [queries and pagination](/pt-br/guides/queries).

Only the generated document is deleted. An interrupted request can leave it behind; reconcile its outcome before retrying. The SDK sends each request once and this contract does not support mutation deduplication or `Idempotency-Key`. See [errors](/pt-br/guides/errors).

</div>
