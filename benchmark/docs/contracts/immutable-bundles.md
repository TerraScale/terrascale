# Immutable contract bundles

The checked-in `contract-archives/` directory is the source of truth for the
published contract bytes in this C# checkout. The current solution consumes
the reviewed release artifact and its sidecar digest; archive publication is a
release operation, not a local build step. Do not recreate an archive with an
undocumented command.

Each archive is rooted at its contract version and has deterministic metadata
(sorted paths, zero timestamps and neutral ownership). The archive and its
sidecars are checked in together so the C# host and protocol tests consume one
reviewed byte set.

For each version, `<version>.tar.sha256` and `<version>.tar.json` record the
archive SHA-256, contract version, manifest SHA-256, and artifact count. A
consumer handoff consists of the archive URL and the exact `archiveSha256`
value. SDK and CLI consumers verify the downloaded bytes before extraction and
then verify each artifact against the manifest.

An existing archive is immutable. A changed contract receives a new versioned
archive, and release automation uploads those exact bytes while passing the
sidecar digest to each consumer check.
