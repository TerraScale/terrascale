# Native package distribution

The official source repository is
`https://git.terrascale.tech/TerraScale/ts-dotnet`. CI builds and tests both
frameworks, packs once and qualifies package-only consumers from those exact
bytes. The candidate version is `0.1.0-native.g<FULL_SOURCE_SHA>`; it is a CI
candidate identity, not a published NuGet version.

`native_package_artifact.py create` requires a clean checkout matching the
source revision and verifies the official nuspec repository/commit, package
ID/version, exact six native inputs and both framework assemblies. The
export contains the `.nupkg`, `SHA256SUMS`, `native-package.json` and the two
installed-consumer receipts. Metadata records the package hash/size, source
revision and hashes of native inputs and assemblies. CI exports only after
both package consumers pass.

The workflow pins Forgejo's `upload-artifact` fork at v3-node20 commit
`ed48a7bb0c8a9079492c354d37994c413423c83e`, which uses Node 20 and the legacy
artifact protocol (`@actions/artifact` 1.1.2). It does not use GitHub's v4
protocol. The first normal PR CI run must confirm this instance's actual
artifact upload and download; source inspection alone is not delivery evidence.
The artifact name includes the full source SHA and run ID. Use `fj` to inspect
the official repository's run, then download that run's artifact from Forgejo's
Actions UI. Retain its run URL and independently reviewed package identity.
The installed `fj` has no artifact-download subcommand.

After downloading and extracting into an owned directory, verify against the
independently retained package SHA-256, version and source SHA. The verifier
can create a new local feed containing only the verified official package:

```bash
python3 scripts/native_package_artifact.py verify \
  --directory /owned/downloaded-artifact \
  --source-revision FULL_SOURCE_SHA --version EXACT_VERSION \
  --sha256 QUALIFIED_PACKAGE_SHA256 --feed /owned/new-native-feed
```

The CLI should pin that exact `PackageReference` version and map
`TerraScale.DotNet` to the verified feed; external dependencies can continue to
use NuGet.org. CI candidate retrieval never copies SDK source or introduces a
project-reference compatibility layer. A changed package requires fresh
package evidence, even when its native source is inherited unchanged.

This change prepares artifact distribution. It does not push to NuGet,
create a release, attach release assets, or claim public availability. A later
approved release must distribute the same qualified bytes and retain their
hash. Actions retention is finite, so durable public dependency availability
remains a release gate. Historical package identities remain historical and
must not be relabeled as current source evidence.
