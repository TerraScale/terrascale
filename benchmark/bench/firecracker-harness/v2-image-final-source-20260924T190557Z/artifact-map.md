# Reviewed v2 image workspace source packet

Status: source frozen for later build gates; **no Make, Cargo, host test, guest test, OCI build, or deployment was run for this packet**. The benchmark guest campaign owns the current heavy slot.

## Source identity and closure

- ts-benchmark revision: `5685f163bb7e9339e8eb0c839fe2512aaf562648` plus the hash-pinned uncommitted image workspace source below.
- Read-only source root: `firecracker-harness/` in this packet. It contains all 42 non-`target/` files from the reviewed live image workspace: producer `fc-image-builder` source and build script, controller `fc-harness` source and examples, `fc-guest-agent`, their manifests, workspace `Cargo.toml`/`Cargo.lock`/toolchain, Makefile, pins, both v1/v2 image recipes, package research JSON, and documentation. The original 2.2-GiB `target/` build cache was excluded. No source symlinks were present. No moving `terra-rust` analyzer/runner source was copied.
- Complete `snapshot-source.sha256` manifest: 42 files, SHA-256 `20bddfafde28c73f164a9e6ed5f645bbd3d8a27a0111360a6080fa4c12c1bc96`. `live-before.sha256`, `snapshot-source.sha256`, and `live-after.sha256` are byte-identical; `snapshot-source-check.log` records a successful check of every copied file.
- Reviewed v2 recipe `images/terrabase-noble-podman-v2/recipe.toml`: SHA-256 `1b86e554c3be605f61403d478757ea4e30b207af6fff1d093dd4ae70a142c385`. Its `rust_cli_builder.source_tree_sha256` pin is `2ce6d748efaefc8d7eba86b08dc3d0ba4101bbe15cc16f46022b287085a7de41`. An independent source-tree digest using the recipe's sorted relative-path/length/bytes algorithm reproduced this value over the copied 19-file `fc-image-builder` crate tree.
- Recipe workspace pins match the copied files: workspace `Cargo.toml` SHA-256 `247ce2e3929de584c4f5dfa6ef1e188e334dbae8bd8ebe74cb5fa48ab77aafac`, workspace `Cargo.lock` `19f498ba6d461985457ca8e45f8d799eb2b6fb25514dd3e50c0ec8e99090afda`, and `rust-toolchain.toml` `448886c2bcf9939abf979026832834cd8630d5a66f3472eea3cbe277c283c445` (Rust 1.98.1). The v2 recipe's output executable, provision receipt, and compile receipt pins are still empty because no such artifacts have been produced.
- Compiler helper `src/cli_compiler.rs` SHA-256 `3fb40328475e6bf675036d6010e01b22d68c6587a5faff745c56ce4d50cc957a`. Producer files reviewed with it: `src/build.rs` `62be08374a49adf83b6ee892548fb12eb61b6cabfe5ecfc3ce819198c4e73f5f`, `src/qualify.rs` `6a39a923135ddab03d402301f1b03c349bd385e6114a40ab32e22519b639a373`, `src/manifest.rs` `58b1d15c3de70af975e2117b19b2499c45548aec6706c21bb3147eca9c4d4350`, `src/workflow.rs` `0427173d419a2c8d677f86084153e0ec6dfc6c1742461a859be10d6681cf216f`, `src/recipe.rs` `a8f22ba3afce1a6452ea4115c30df55ffb0ee4c0b65ad7de74c406a2c1af194e`, and `src/main.rs` `3138e97faead05ab406955e5e3d27103dbc09109f6b12c6e5a4c219a7b1f3533`.
- The producer parser `src/firecracker_release.rs`, controller `src/image_profile.rs` and `src/main.rs`, and agent `src/main.rs` are also in the complete manifest. Literal `include_bytes!`/`include_str!` inputs—v1 recipe variants, candidate research JSON, and `pins.toml`—are present at their original relative paths. `fc-image-builder/build.rs` uses the active Rust/Cargo toolchain version, with no external source file read at build time.

## Queued verification scope

Run these serially from the read-only packet's `firecracker-harness/` directory, with `CARGO_TARGET_DIR` outside the packet and `ionice -c 3 nice -n 19 make -C <packet>/firecracker-harness <gate>`. The Make targets use low CPU/I/O priority, Cargo `--locked`, and two build jobs. Retain each log and an exit-status inventory, then recheck `snapshot-source.sha256` and hash the resulting musl test executables. The approved compile gate sequence is:

1. `fmt`
2. `check-image-builder`
3. `lint-image-builder` (`--all-targets -D warnings`)
4. `test-build-image-builder` (musl `cargo test --no-run --lib`)
5. `check-controller` (`fc-harness` and `fc-guest-agent`)
6. `lint-controller` (`--all-targets -D warnings`)
7. `test-build` (musl `fc-harness` tests, `--no-run`)

`build-image-builder`, `build-controller`, and `build-guest` are separate follow-on artifacts for the real CLI/controller/guest caller. VM test execution must use the Firecracker `test-vm` path with pinned static payloads. Producer receipt/provision and OCI export compatibility still require actual runtime validation. The old `v2-release-gates-20260924T172251Z` logs predate the reviewed resolver correction and must not be treated as passing evidence for this source packet.
