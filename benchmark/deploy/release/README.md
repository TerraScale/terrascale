# Release packaging

This directory builds the two repository-owned runtime packages:

- `terrabase-server`, the native TBEP server package; and
- `terrabase-studio`, the loopback-only operator UI.

The server package is not, by itself, production admission. Protected signing
requires an independently signed capability statement bound to the same Git
commit and tree. That statement must carry passing evidence for real cluster
composition and encryption at rest. The current standalone server cannot
supply that evidence, so its unsigned package can be tested but it cannot be
promoted.

`package.sh` accepts one component and one native Rust target. It refuses
cross-compilation and `target-cpu=native`, builds from locked dependencies,
creates a deterministic binary archive, and creates an OCI archive from the
same binary. The OCI base is pinned by digest. Each output also contains a
CycloneDX SBOM, in-toto provenance, a release manifest, and `SHA256SUMS`.

`reproduce.sh` builds twice and compares the complete unsigned output.
`sign.sh` signs `SHA256SUMS` with an ECDSA key supplied by the protected
release environment and verifies the signature against the independently
configured public key. Keys are never copied into an artifact. For the server,
`require-production-capability.sh` verifies a separate ECDSA P-256 signature,
the source binding, and the two evidence digests before signing. `verify.sh`
rechecks the checksums, signatures, SBOM and provenance bindings, native
archive binary, OCI platform, and entrypoint.

The Forgejo release workflow runs on `develop` without secrets. Native runners
build each package twice, compare the unsigned outputs, and retain the exact
bytes. The protected manual promotion workflow runs only when its selected ref
is `main`. It downloads those exact artifacts and independent capability
evidence by workflow-run ID, checks that every source binding equals the event
SHA, and adds signatures without rebuilding.

The capability artifact is named `terrabase-production-capability` within its
selected workflow run and contains:

- `PRODUCTION-CAPABILITY.json` and its detached
  `PRODUCTION-CAPABILITY.sig`;
- `cluster-composition.evidence.json`, with capability
  `real-cluster-composition` and status `passed`; and
- `encryption-at-rest.evidence.json`, with capability
  `encryption-at-rest` and status `passed`.

Both evidence documents and the signed statement must name the release commit
and Git tree. The verification key is independent from the release signing
key. No repository-owned workflow fabricates this external qualification
evidence.

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
