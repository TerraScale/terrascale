# ts-dotnet package pin

The dashboard references the official `TerraScale.DotNet` SDK as a NuGet
package. It does not reference a sibling checkout or the TerraBase project.
`NuGet.Config` maps this package exclusively to this committed feed; other
dependencies come from NuGet.org. The application solution contains one project.

`provenance.json` records the exact bytes and source revision. This is a local
dashboard candidate built from an unmodified, clean `git archive` of the official
ts-dotnet source, using .NET 10.0.401. It is not a public or qualified SDK release.
Do not relabel it as the SDK's two-framework/native-consumer qualified artifact.
An official durable release and real-service consumer qualification remain
cutover gates.

The pack command, run from the extracted archive, was:

```sh
dotnet pack src/TerraScale.DotNet/TerraScale.DotNet.csproj --configuration Release \
  -p:PackageOutputPath=/absolute/path/to/dashboard/vendor/dotnet \
  -p:Version=0.1.0-dashboard.gdf852e3b4d3a4df324452d46792b20566eea5136 \
  -p:RepositoryCommit=df852e3b4d3a4df324452d46792b20566eea5136 \
  -p:RepositoryUrl=https://github.com/TerraScale/ts-dotnet
```

Prefix local pack/build/test commands with `nice -n 19`.
Run `python3 scripts/dotnet/verify-sdk.py` to verify the package hash, nuspec
identity, repository metadata, assembly, and application package reference.
