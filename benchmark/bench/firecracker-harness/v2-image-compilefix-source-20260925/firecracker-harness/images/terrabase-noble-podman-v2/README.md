# terrabase-noble-podman-v2 candidate

This is a separate, unqualified candidate. The v1 recipe is preserved as an
archive-only input, and the prior harness profile remains historical evidence.
The current image builder accepts only the v2 recipe; the harness still uses
its historical image pins unless a verified profile is selected explicitly.
The v2 kernel source is the
kernel.org Linux 6.18.53 tarball, whose detached signature is verified against
the decompressed tar before extraction. Its pinned release timestamp supplies
`SOURCE_DATE_EPOCH`.

The Firecracker source policy and 6.18 config seed are pinned to commit
`95f868c8e345b1cc8faccd1a3c910b4989dc3f58`. The policy supports the 6.18
version line, while warning that Firecracker's CI config targets Amazon Linux
kernels. The candidate therefore requires a generated, reviewed upstream
`.config` and diff before Fetch/Build can proceed. The APT research packet is
only a graph candidate; package solver receipts and the derived Noble builder
digest are also incomplete.

The official Firecracker 1.17.0 x86_64 archive is locked to the GitHub release
asset API digest and size. Its release tag is unsigned, so this records the
archive metadata and extracted executable identity without claiming a source
build or cryptographic release signature. The archive member path and
executable path and hash are candidate-observed pins. The v2 bootstrap verifies
them against the downloaded archive and leaves the generated kernel config
pending independent review. Fetch records the one permitted redirect from the
exact GitHub release URL to `release-assets.githubusercontent.com`; all other
source fetches remain redirect-disabled.

No service image, VM boot, RustFS/Von behavior, or multi-node replication is
qualified by this recipe. Do not switch the default harness profile to v2
until the separate qualification receipt and real guest run exist.
