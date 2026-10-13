# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.3 from accepted merged SDK source
`ff1d1126087d40d112ca27383fbb3111e320cb4a`. NuGet.Config maps the package
exclusively to this committed feed; other dependencies come from nuget.org.
The package supports net10.0 and net11.0; the dashboard targets net10.0.

The source, toolchain, package/assembly hashes and all 70 raw contract hashes
are recorded in provenance.json. Two clean locked builds with independent caches
produced identical bytes, and installed-package HTTP-fake consumers passed on
both frameworks. Contracts retain their relative directories, including binary
producer archives and explicitly unmerged producer inputs.

The v0.0.3 tag points at that accepted source. [Release run 38347030566](https://github.com/TerraScale/ts-dotnet/actions/runs/38347030566)
published to GitHub Packages. A fresh-cache registry restore verified the package
SHA256 `bab60bfefffc49c878e93a300cf260be6d558ae22e1e942e68277c3badad7845`
and passed both installed consumers. Packaging pending producer inputs does not
accept them, and fake tests do not prove live service compatibility.

Run `nice -n 19 python3 scripts/verify-sdk.py` to check the recorded package,
repository identity, framework assemblies, contract paths/hashes and central pin.
