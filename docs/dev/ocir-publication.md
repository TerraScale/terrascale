# Regional application images

GitHub Actions publishes the same Linux arm64 manifest to GHCR and to
`sjc.ocir.io`, `fra.ocir.io` and `gru.ocir.io` during the registry migration.
The OCIR repository is `<namespace>/terrascale/<pkg>`, where the package is
`terrabase`, `ts-auth` or `terrascale-dashboard`. `OCIR_NAMESPACE` is the
repository variable identifying the OCI tenancy's registry namespace.

The publisher copies the manifest by digest instead of rebuilding it in each
region. It checks the destination digest, then attaches the build provenance
and SBOM bundles with ORAS. Each attachment has the image manifest as its
subject, artifact type `application/vnd.dev.sigstore.bundle.v0.3+json`, and
exactly one JSON blob layer. ORAS uses the OCI referrers API or the OCI
referrers tag schema supported by the registry. The publisher discovers every
attachment, checks its subject and artifact type, downloads its layer and
compares the bytes with the original signed bundle.

Before regional publication, each publisher job exercises the same commands
against three repositories in its local registry service, using its actual
image and GitHub-signed bundles. This proves the registry transfer without
requiring OCI credentials on a development machine.

Releases keep the existing keyless cosign selection mechanism. The workflow
signs a separate selection with predicate type
`https://terrascale.tech/attestations/release-selection/v1` and the service and
revision fields. It retrieves that new bundle from GHCR and attaches the same
bytes in every OCIR region. All regional selection attachments must verify
before any regional `:release` pointer moves. GHCR publication and its release
pointer continue during migration.

Each `:release` tag identifies the single-platform manifest directly. The
release observer receives `<namespace>/terrascale/<pkg>@sha256:<digest>`
without a registry host. Hosts discover the regional release tag, prepend
their own `<region>.ocir.io`, and verify provenance and selection offline
using the existing Sigstore trusted root and workflow identity policy.
Build-only publication does not create a selection or move a release tag.
