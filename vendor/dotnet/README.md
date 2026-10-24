# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.7 from merged ts-dotnet revision
`91bba25fa61e327f7b8628be98dc76b702779b8f` (PR58). PR57 added query explain
and nullable query-shape latency buckets and p95. Logs renders a missing p95 as
an em dash and sorts that shape by maximum latency, so older TerraBase responses
remain usable while the server rollout is pending.

The package was built with the SDK's `scripts/pack-reproducible.py`: two clean
locked builds produced identical bytes, and installed-package HTTP fake consumers
passed on net10.0 and net11.0. It is vendored, not claimed to be published to a
registry. NuGet.Config maps this package exclusively to the committed feed.

`provenance.json` records the source revision, toolchain, package and assembly
hashes, and every packed contract hash. CI runs `scripts/verify-sdk.py` to check
those bytes and the central package pin. HTTP fakes do not prove live service
compatibility.
