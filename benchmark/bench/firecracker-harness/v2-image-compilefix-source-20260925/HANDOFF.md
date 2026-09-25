# v2 image-builder compile-fix source derivative

Status: source-frozen candidate awaiting independent review and coordinator acceptance. This packet does not qualify an image. Cargo, container, image, VM, and APT-helper commands have not been run for this derivative.

The immutable base packet is v2-image-final-source-20260924T190557Z, at benchmark repository HEAD 5685f163bb7e9339e8eb0c839fe2512aaf562648. Its 42-file source manifest is preserved as sealed-base-source.sha256 (SHA-256 20bddfafde28c73f164a9e6ed5f645bbd3d8a27a0111360a6080fa4c12c1bc96). This derivative keeps the same 42 relative source paths. Its current content manifest is source.sha256 (SHA-256 2fc0ee4c871a356608476a78b84b32ce52e2644aae5aaae988d302bf9b7a1fdc).

The copied base-HANDOFF.md, base-source.sha256, and base-provisional-source.sha256 are preserved prior-stage metadata from the APT-helper derivative; they are not the source seal for this packet. Both old manifest copies hash to 9003630ad3b3c017c47b8e21b1d3e3dfd8e4f4ab8a557d1b401be86fab9d9149 and intentionally describe that earlier stage.

Relative to the immutable base, exactly 16 of the 42 files differ:

- firecracker-harness/README.md: 3b37d0d154ca0486fee3c4087a8fb7228d49261f9ef40a436b67d73da143678d
- firecracker-harness/crates/fc-image-builder/src/apt.rs: 674b15ca143ec91f02d84f73e99131a85dfeae569dd0a52d329ddbe1a62d2638
- firecracker-harness/crates/fc-image-builder/src/build.rs: d2154de334891475b047c7a2bca95376b061966399a1f3ce00a98a1841f96f2b
- firecracker-harness/crates/fc-image-builder/src/cli_compiler.rs: 5ff564db2aaba4b3932442de52ddd23e102712b50082c83f1733b2d484d1d01e
- firecracker-harness/crates/fc-image-builder/src/input.rs: c48a1372d926367d67d04674cdc41ef5b3b5da45b1c7e8d453abfd6571040f8e
- firecracker-harness/crates/fc-image-builder/src/main.rs: 6d7003785531f369ce7ef6d55cd26d804959676e3e50b2c69314a07bde7480a3
- firecracker-harness/crates/fc-image-builder/src/oci_archive.rs: 74df6a6d5fa396f52251120391add035d0c281611f28744202b0d9ccd9c955db
- firecracker-harness/crates/fc-image-builder/src/package_lock.rs: 4fbadb63b85446e250316eb0127a2f25452e583d4c1673e04bbbc1818b20821d
- firecracker-harness/crates/fc-image-builder/src/podman.rs: e3bbefeb4fbc2f044b95a4b5eb02f50e3aeef3de3db4cdd953f86870b089a7bf
- firecracker-harness/crates/fc-image-builder/src/qualify.rs: 243b20c563aea6ee150187d7aac62f2ef666f2b4b130c47802c41c62d8136c07
- firecracker-harness/crates/fc-image-builder/src/recipe.rs: d6d8f41580b327c3c94d3c70bbe53549e3f18cd1c81801c7714a0318174198f8
- firecracker-harness/crates/fc-image-builder/src/signature.rs: f0371fd1e7f57448d5c58d90d54c148416488d6fa74e1a1ed60733d216261888
- firecracker-harness/crates/fc-image-builder/src/workflow.rs: 32d83463cf1fd221c1595f13899f59c79dfcb6bcd5d776a9dcf1b135ee262309
- firecracker-harness/images/terrabase-noble-podman-v1/README.md: 02b5d9edc5ab10484d8e516302346b201553cfce7d86a41d022f47efa908193f
- firecracker-harness/images/terrabase-noble-podman-v2/README.md: b620fc7c322a05fe24bf50ce13924d5fe46b8145e750cbe76622bef1572dd8cc
- firecracker-harness/images/terrabase-noble-podman-v2/recipe.toml: 08efa54e9137679e747d670f0bd1c922c577af24dcb7cfdb66d1976aa9f43458

The active recipe parser, package-lock identity check, and qualification path use v2; the old Git kernel fetch/build path has been removed. Historical v1 manifest validation remains for evidence. Both v1 recipe files remain byte-for-byte identical to the immutable base: recipe.toml is 1b1b5ab2b44f55a51b89215120b201ef81a02edfbe3ff4164479f0af445fc3de, and recipe-v1.0.0.toml is e1fa94b01f56f37f8f3e80253076bfd379c7b0264f7b04c83fd76c550b10ee37. The v1 README describes those recipes as archive-only. The v2 candidate remains explicitly unqualified and is not the default harness runtime profile.

The v2 recipe Rust CLI source-tree pin is 1bec6d5f101ed70179ac53024a1ff1370418d7786153df34275cad6bbaa38c39. It was recomputed over the 19 regular files in firecracker-harness/crates/fc-image-builder, using the implementation's sorted UTF-8 relative-path / big-endian length / file-bytes algorithm, and matches the value in the v2 recipe. The separate live builder tree hashes to c589be7087b754190bdd0ed198f7818c33f3033e0eea0a5d650939adaa61f7d6; it is a different source state and was not overlaid into this derivative.

Verification on this packet: ionice -c 3 nice -n 19 rustfmt --check --edition 2024 firecracker-harness/crates/fc-image-builder/build.rs firecracker-harness/crates/fc-image-builder/src/*.rs passed. Both v1 recipe hashes matched the immutable base. source.sha256 contains the same 42 relative paths as the immutable base. Run sha256sum --quiet -c source.sha256 from this directory to verify the derivative files. No Cargo build or tests have been run; the compile-only Makefile gates remain pending independent review and the coordinator's build-window sequencing. No image or VM qualification is claimed.
