# Regional application images

GitHub Actions publishes the same Linux arm64 manifest to GHCR and to
`us-sanjose-1.ocir.io`, `eu-frankfurt-1.ocir.io` and
`sa-saopaulo-1.ocir.io` during the registry migration. Staging also receives
the same digest at `us-sanjose-1.ocir.io/<namespace>/terrascale-stg/<pkg>`.
The production OCIR repository is `<namespace>/terrascale/<pkg>`, where the package is
`terrabase`, `ts-auth` or `terrascale-dashboard`. `OCIR_NAMESPACE` is the
repository variable identifying the OCI tenancy's registry namespace.

The shared `ocir-login` action exchanges the job's GitHub OIDC token for an
OCI user principal session token, then obtains short-lived registry tokens.
It uses the repository's identity-domain URL, exchange client ID and exchange
client secret. No long-lived OCI API key or registry password is installed.
Only the main workflow identity can publish to OCIR.

The publisher copies the manifest by digest instead of rebuilding it in each
region. It checks the destination digest, then attaches the build provenance
and SBOM bundles with ORAS. Each attachment has the image manifest as its
subject, artifact type `application/vnd.dev.sigstore.bundle.v0.3+json`, and
exactly one JSON blob layer. ORAS uses the OCI referrers API or the OCI
referrers tag schema supported by the registry. The publisher discovers every
attachment, checks its subject and artifact type, downloads its layer and
compares the bytes with the original signed bundle.

Before regional publication, each publisher job exercises the same commands
against four repositories in its local registry service, using its actual
image and GitHub-signed bundles. This proves the registry transfer without
requiring OCI credentials on a development machine.

The existing PR lint job runs a cheaper transport proof with a public arm64
image and an unsigned dummy bundle. Its registry has no referrers API, as
OCIR does not. It checks the identical digest, the standard
`sha256-<hex>` referrers index and a downloaded bundle. An invalid attachment
must leave `:release` absent before the valid attachment allows it to move.
This transport proof makes no signing or identity-verification claim.

Releases keep the existing keyless cosign selection mechanism. The workflow
signs a separate selection with predicate type
`https://terrascale.tech/attestations/release-selection/v1` and the service and
revision fields. `cosign attest-blob` signs the exact raw child manifest into
a bundle file after its SHA-256 is checked against the image digest. ORAS
attaches those same bundle bytes in all four OCIR destinations and GHCR.
All selection attachments must verify
before any regional `:release` pointer moves. GHCR publication and its release
pointer continue during migration.

Each `:release` tag identifies the single-platform manifest directly. The
release observer receives `<pkg>@sha256:<digest>`. Hosts discover the regional
release tag and expand the package using their own registry host, namespace
and environment repository prefix. They verify provenance and selection offline
using the existing Sigstore trusted root and workflow identity policy.
Build-only publication does not create a selection or move a release tag.

Before this producer merges, the parent producer change must be on main and
the login action must be pinned to C41's main merge commit. C42 must also
publish a qualified `ci-` orchestrator release whose `release wait` accepts
the package-only reference. Record that exact release in the producer PR and
confirm the shared release downloader selects it before enabling auto-merge.
Older clients reject the new reference after release pointers have moved.

An OCIR authentication, copy or attachment failure stops publication before
the GHCR release pointer moves. Until corrected, that failure blocks app
releases; there is no automatic GHCR-only bypass. Roll back through a PR that
reverts this producer change and coordinate the receiver's registry contract
with C42. Already-published OCI release pointers are not deleted by a revert.
