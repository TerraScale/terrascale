# Release packaging

This directory builds the two repository-owned runtime artifacts:

- `terrabase-server`, the authenticated production TBEP daemon; and
- `terrabase-studio`, the loopback-only operator UI.

`package.sh` accepts one component and one native Rust target. It refuses
cross-compilation and `target-cpu=native`, builds from locked dependencies,
creates a deterministic binary archive, and creates an OCI archive from the
same binary. The OCI base is pinned by digest. Each output also contains a
CycloneDX SBOM, in-toto provenance, a release manifest, and `SHA256SUMS`.

`sign.sh` signs `SHA256SUMS` with an ECDSA key supplied by the protected
release environment and verifies the signature against the independently
configured public key. Keys are never copied into an artifact. `verify.sh`
rechecks the checksums, signature, SBOM and provenance bindings, OCI platform,
and entrypoint.

The Forgejo release workflow runs only for the protected `develop` to `main`
candidate pull request. Native runners build the same source twice and compare
the unsigned outputs before signing one copy. A main-branch promotion must
consume those exact artifacts. It must not rebuild them.

Studio remains unauthenticated and refuses non-loopback binds. Its image is
for an operator workstation using host networking; it is not a public service
or a cluster deployment target.

The required protected runner labels are:

- `self-hosted`, `protected`, `terrabase-release-amd64`
- `self-hosted`, `protected`, `terrabase-release-arm64`

Runner registration, signing-key custody, candidate admission, publication,
and rollout are external infrastructure gates owned by TerraScale.Deploy and
TerraScale.Orchestrator. A queued job or locally generated signature is not
release evidence.
