---
title: C# TerraBase quickstart
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# C# TerraBase quickstart

Run the [C# console starter](https://git.terrascale.tech/TerraScale/ts-docs/src/branch/main/examples/terrabase-dotnet)
to create, read, update, query and delete a JSON document through
`TerraScale.DotNet.TerraBase.TerraBaseDocumentsClient`. Every database request
uses the SDK; application data stays in TerraBase.

::: warning Candidate compatibility, not public launch
The starter targets the project-scoped C# API and an exact SDK candidate.
Public NuGet installation, automatic Atlas integration and production ts-auth
JWT authorization are separate release gates. The isolated compatibility
fixture uses the matched binary's existing credential contract; it is not
an instruction to provision production credentials or duplicate ts-auth's
users, organizations or projects. Dashboard sign-in stays on its native pages.
See [SDK compatibility](/hi/guides/sdks).
:::

## 1. Obtain the matched SDK

Install .NET SDK 10 or newer, with the .NET 10 runtime installed. Obtain an immutable `TerraScale.DotNet` package containing
`TerraBaseDocumentsClient` and record its exact version and SHA-256. Public NuGet
availability must be verified before presenting an install command with a
released version; a source version alone does not establish publication.
An older `TerraScaleClient` package or the legacy UUID/shard `NativeClient`
contract is not an interchangeable dependency.

Copy `examples/terrabase-dotnet` from the docs checkout. The starter consumes
only a package reference, with no SDK source reference or custom API transport.

## 2. Configure a provisioned database

Export these values in your private shell; `.env.example` lists the settings
but the console app does not load dotenv files:

```bash
export TERRASCALE_API_URL='https://<region>.api.terrascale.tech'
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ID
export TERRASCALE_DATABASE_NAME=quickstart
export TERRASCALE_COLLECTION_NAME=posts
# Set TERRASCALE_ACCESS_TOKEN through your secret manager or private shell.
```

Use an explicit HTTPS regional origin from the allocation's supported routing
workflow. The project, database and collection must already exist, with read
and write authorization. The starter neither creates identity resources nor
implements a credential exchange. Production credentials come from ts-auth's
supported flow and must be qualified against the serving API. Keep secrets out
of source control, browser public settings and console output.

## 3. Build and run

From the copied starter directory:

```bash
dotnet build -c Release -p:TerraBaseSdkPackageVersion=EXACT_VERSION \
  --source /absolute/path/verified-sdk-feed --source https://api.nuget.org/v3/index.json
dotnet bin/Release/net10.0/TerraBaseStarter.dll
```

Prefix local builds/checks in this repository with `nice -n 19`.
Success prints `PASS_TERRABASE_CRUD_QUERY`: the created document was read,
updated and found by its query, a stale version was rejected, and the document
was deleted and read back as absent. The application has a 60-second overall
deadline and 15-second request deadlines. It prints neither document bodies,
credentials nor arbitrary server error messages.

## 4. Preserve versions and bound queries

The starter generates one document and carries its opaque version through
`TerraBasePrecondition.Matches(version)` for update and deletion. A concurrent
change produces `TerraBasePreconditionFailedException`; refetch and resolve
that conflict before attempting another write. Do not parse ETags, invent a
version or blindly retry an interrupted mutation. The matched SDK sends each
request once and the producer rejects `Idempotency-Key`.

`TerraBaseQuery` uses JSON filter grammar, such as an equality filter over a
JSON Pointer. It is distinct from older SQL/live-view examples. Query pages
carry `NextCursor`; pass that opaque cursor with the same filter/sort settings
when requesting another page. The example uses a unique run field and a limit
of 20, so it can verify its own document without inspecting unrelated data.

Only the generated document is deleted. An interrupted request can leave it
behind; reconcile the outcome rather than deleting unrelated resources.
Disposing the caller-owned `HttpClient` closes its transport.
`TerraBaseHttp.CreateHandler()` disables redirects and cookies.

## 5. Qualify against the real binary

From the docs checkout, run the package-only qualification lane described in
the [starter README](https://git.terrascale.tech/TerraScale/ts-docs/src/branch/main/examples/terrabase-dotnet/README.md):

```bash
nice -n 19 npm run qualify:dotnet -- \
  --package /absolute/path/TerraScale.DotNet.EXACT_VERSION.nupkg \
  --sha256 SDK_SHA256 --version EXACT_VERSION \
  --testing-package /absolute/path/TerraScale.DotNet.Testing.EXACT_VERSION.nupkg \
  --testing-sha256 TESTING_SHA256 \
  --binary /absolute/path/matched-terrabase-binary \
  --binary-sha256 PRODUCER_SHA256 \
  --receipt /absolute/path/private-evidence/csharp.json
```

The runner verifies artifact hashes, installs the exact packages into a fresh
consumer/cache, and uses the SDK's testing helper to start the real pinned
binary. It runs this same starter journey, verifies read-only write denial,
invalid-credential rejection and absence of live documents after cleanup.
Missing or mismatched inputs fail; no emulator or stub substitutes for a binary.

A receipt identifies the SDK, testing package, producer and example source
bytes. It qualifies only this isolated compatibility journey. Public package
availability, deployed ts-auth JWTs, native dashboard sign-in, real Atlas
resolution and multi-region behavior still need their own evidence.
[Atlas discovery](/hi/guides/atlas) describes the project-scoped discovery contract
and the SDK integration boundary.
