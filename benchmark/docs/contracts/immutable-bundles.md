# Immutable contract bundles

`terrabase-contracts` is the only producer of the public contract directory.
After generation, `make contract-archive` packages `contracts/` into
`contract-archives/v<version>.tar`. The tar entries are rooted at the contract
version and have deterministic metadata (sorted paths, zero timestamps and
neutral ownership), so the same producer bytes produce the same archive.

The target also writes `<version>.tar.sha256` and `<version>.tar.json`. The
sidecar records the archive SHA-256, contract version, manifest SHA-256 and
artifact count. A consumer handoff consists of the archive URL and the exact
`archiveSha256` value. TypeScript, Go, Rust, .NET and CLI installers verify the
downloaded bytes before extraction and then verify each artifact against the
producer manifest.

An existing archive is immutable: rerunning the target succeeds only when the
new bytes and sidecars are byte-identical. A changed source must receive a new
versioned archive. The archive is not a substitute for publication; release
automation must upload these exact bytes and pass the sidecar digest to each
consumer check.
