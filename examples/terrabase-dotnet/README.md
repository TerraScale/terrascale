# C# TerraBase starter

Use the official `TerraScale.DotNet.TerraBase.TerraBaseDocumentsClient` to create,
read, version-check, update, query and delete one JSON document. It sends every
request through the SDK and keeps application data in TerraBase.

Requires .NET SDK 10 or newer with the .NET 10 runtime installed, a verified
C# SDK candidate package/feed, an explicit
HTTPS regional API origin, and a provisioned project/database/collection with
read/write authorization. Export the settings in `.env.example`; no dotenv
loader is included. The application does not create users, organizations or
projects. Public NuGet publication, automatic Atlas integration and the
production ts-auth JWT journey are separate release gates.

```bash
dotnet build -c Release -p:TerraBaseSdkPackageVersion=EXACT_VERSION \
  --source /absolute/path/verified-sdk-feed --source https://api.nuget.org/v3/index.json
dotnet bin/Release/net10.0/TerraBaseStarter.dll
```

The candidate must contain `TerraBaseDocumentsClient`; neither an older
`TerraScaleClient` package nor the legacy UUID/shard `NativeClient` contract is
an interchangeable dependency. Do not infer publication from a source version.
Prefix local builds/checks in this repository with `nice -n 19`.

Success prints `PASS_TERRABASE_CRUD_QUERY`: the updated document matched its
query, a stale version was rejected, and the document was deleted and read back
as absent. No document payload, credential or raw server error is printed.
The app uses a 60-second overall deadline and 15-second request deadlines.
Only its generated document is deleted. An interrupted request can leave that
document behind; reconcile its outcome rather than blindly replaying a write.

Use the package-only real-process qualification lane from the docs checkout:

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

The SDK's test helper verifies and starts the pinned real binary with an
isolated allocation. Missing or mismatched inputs fail; there is no stub mode.
The helper's compatibility fixture is not a production credential-provisioning
workflow. Real-process compatibility does not qualify JWT sign-in, public
package availability, automatic Atlas discovery, replication or billing.
