# Provisional APT helper source derivative

Status: final source seal accepted after independent source review; this is not an image qualification. No Cargo, container, VM, image build, or APT helper execution was run.

The source closure is the reviewed 42-file packet at `v2-image-final-source-20260924T190557Z`, whose immutable source manifest SHA-256 is `20bddfafde28c73f164a9e6ed5f645bbd3d8a27a0111360a6080fa4c12c1bc96`. The derivative contains the same 42 relative paths. Its content manifest is `provisional-source.sha256`, SHA-256 `9003630ad3b3c017c47b8e21b1d3e3dfd8e4f4ab8a557d1b401be86fab9d9149`. Relative to the base packet, exactly five files differ:

- `firecracker-harness/crates/fc-image-builder/src/apt.rs`: `674b15ca143ec91f02d84f73e99131a85dfeae569dd0a52d329ddbe1a62d2638`
- `firecracker-harness/crates/fc-image-builder/src/build.rs`: accepted config-validation change, `4266cb46d311120e41e19c1083c67a2294755e875250826572cc529c8825411a`
- `firecracker-harness/crates/fc-image-builder/src/qualify.rs`: `f72571162dc46220605d96d93072146d2d7a657246c2416cebb04d093bc4918a`
- `firecracker-harness/crates/fc-image-builder/src/recipe.rs`: `5042ac2637ea63a20e968e0fe2347db4d3c14c09cff015ec32235591de70b020`
- `firecracker-harness/images/terrabase-noble-podman-v2/recipe.toml`: `97076beee182897e614b1942e051b19bf0da1fdbcc119110a576ab9cbecf3e8c`

The original v1 recipe remains byte-for-byte unchanged (SHA-256 `1b1b5ab2b44f55a51b89215120b201ef81a02edfbe3ff4164479f0af445fc3de`). Its omitted helper fields deserialize to the reviewed pin for historical compatibility; the stale v1 source-tree pin means no claim is made that v1 qualification remains usable. The unrelated live Makefile edit is excluded.

The initial helper pin is `/usr/lib/apt/apt-helper`, SHA-256 `91aa654ba3f5b68b784dd5ce94d625080fb6f7bdded88885120fd979936bcf8b`, from the pinned Ubuntu amd64 child `sha256:496754492fb28b4d3049432f2ca787449331e23fb14f0dd3fffea86bf5a93eb4`. Inspection evidence remains in `noble-base-apt-helper-inspection-20260924-b`; the helper was not executed. Qualification copies and verifies it, retains signed `Packages.xz` under that exact suffix, then calls `apt-helper cat-file /work/.../Packages.xz` with no network in the pinned child. Existing compressed and decompressed size/hash checks, signed-index verification, package-origin validation, command receipt, and helper path/hash receipt fields remain. Later derived-builder xz use is unchanged.

The v2 Rust CLI source-tree pin is `c589be7087b754190bdd0ed198f7818c33f3033e0eea0a5d650939adaa61f7d6`, computed from the 19-file `fc-image-builder` source tree with the accepted build-script change and final APT edits. The current benchmark repository HEAD is `5685f163bb7e9339e8eb0c839fe2512aaf562648` with unrelated working-tree changes; this packet overlays only the files listed above.

Independent review: `/root/review` accepted the five-file source delta with no concrete defect. The coordinator accepted this packet for source sealing. The reviewer independently checked both 42-file manifests and recomputed the v2 source-tree digest. The final 42-file manifest is `source.sha256`, SHA-256 `9003630ad3b3c017c47b8e21b1d3e3dfd8e4f4ab8a557d1b401be86fab9d9149`; `provisional-source.sha256` is retained byte-identically as the pre-review manifest.

Verification performed: `sha256sum --quiet -c source.sha256` passed for all 42 files; the base packet's 42 hashes also matched its original manifest; the exact five-file overlay set and v2 source-tree pin were recomputed and matched. `ionice -c 3 nice -n 19 rustfmt --check --edition 2024 firecracker-harness/crates/fc-image-builder/src/apt.rs firecracker-harness/crates/fc-image-builder/src/recipe.rs firecracker-harness/crates/fc-image-builder/src/qualify.rs` passed. No compile or runtime checks were run under the coordinator's restriction, and source review does not establish helper execution or image qualification.

Queued compile-only gates for the next free build window, after the active receiver batch or any required fixes, are serial and use the frozen packet Makefile with low CPU/I/O priority: `fmt`, `check-image-builder`, `lint-image-builder`, `test-build-image-builder`, `check-controller`, `lint-controller`, then `test-build`. The latter two test-build targets only compile musl tests with `--no-run`; execution remains a separate Firecracker guest gate. Do not run container, image, or VM qualification as part of these compile gates.
