# TBEP test-harness candidate

TerraBase produces one cross-repository test dependency named
`tbep-test-harnesses`. It is qualification tooling, not a product runtime or a
public package. The only admitted output tree is
`artifacts/dependencies/terrabase-tbep-harnesses`, and it contains exactly two
Linux x86-64 executables:

```text
tbep-production-harness
tbep-fbs-quic-peer
```

The first binary hosts the real durable engine behind the production
FlatBuffers QUIC listener. The second is the one-shot generated-code
interchange peer. Neither binary may be rebuilt from a sibling checkout while
an Internals release test is running.

## Candidate build

`.terrascale/deploy.json` builds each binary once with Rust `1.97.1`,
`--locked`, `--release`, and target `x86_64-unknown-linux-musl`. The two Cargo
manifests retain their separate lock files. Before Cargo runs, the preparation
step requires both candidate-private target directories, the output, and local
bindings to be absent. This prevents Cargo from declaring a restored or
tampered final executable `Fresh`. Dependency compilation caches must use a
compiler cache such as `sccache`, not a restored Cargo target directory.

The packaging step accepts only the literal target and output path, requires a
clean Git tree and the matching preparation binding, and copies only the two
named files. It requires mode `0755` and a parseable, bounded, little-endian
x86-64 static ELF executable. Program interpreters, shared-library
dependencies, runtime library paths, shared objects, executable stacks, links,
and malformed program or dynamic headers are rejected. The copied bytes must
match the fresh build products.

The packager writes an ignored local binding under
`target/tbep-test-harnesses/package-binding.v1`. That record lets local
`verify` runs reject a different source commit or tree, changed Deploy
configuration, wrong target, extra file, link, permission drift, size drift,
or byte tampering. It is not a transferable candidate receipt.

Run the local checks with:

```sh
python3 -m unittest scripts/test_package_tbep_test_harnesses.py
bash -n scripts/package_tbep_test_harnesses.sh
```

The build requires the Rust musl standard library and a musl cross-linker. In a
clean diagnostic checkout, run the same one-shot sequence as Deploy:

```sh
scripts/package_tbep_test_harnesses.sh prepare \
  --repository-root . \
  --target tbep-test-harnesses \
  --output artifacts/dependencies/terrabase-tbep-harnesses
cargo +1.97.1 build --locked --release \
  --manifest-path crates/terrabase-tbep/Cargo.toml \
  --bin tbep-production-harness \
  --target x86_64-unknown-linux-musl \
  --target-dir target/tbep-test-harnesses/production
cargo +1.97.1 build --locked --release \
  --manifest-path crates/terrabase-tbep/tools/tbep-fbs/Cargo.toml \
  --bin tbep-fbs-quic-peer \
  --target x86_64-unknown-linux-musl \
  --target-dir target/tbep-test-harnesses/flatbuffers
scripts/package_tbep_test_harnesses.sh package \
  --repository-root . \
  --target tbep-test-harnesses \
  --output artifacts/dependencies/terrabase-tbep-harnesses
scripts/package_tbep_test_harnesses.sh verify \
  --repository-root . \
  --target tbep-test-harnesses \
  --output artifacts/dependencies/terrabase-tbep-harnesses
```

## Receipt and transfer

Deploy owns the transferable receipt. On a clean committed tree, the producer
command is:

```sh
terrascale-deploy ci build-candidate \
  --repository-root . \
  --target tbep-test-harnesses \
  --expected-tree "$PLAN_TREE" \
  --receipt artifacts/evidence/tbep-test-harnesses.build-receipt.json
```

Deploy checks the expected tree before the build and rechecks the source,
configuration, CLI identity, and output afterward. Its receipt binds repository
`TerraBase`, target `tbep-test-harnesses`, the logical output root, and both
files and digests. The consumer rejects missing or extra files, links,
non-executable files, a different repository, tree, configuration, target, or
receipt, and mutation before or after testing.

The immutable transfer must place the verified tree at
`artifacts/dependencies/terrabase-tbep-harnesses` in TerraScale.Internals and
transfer the receipt as
`artifacts/evidence/terrabase-tbep-harnesses.build-receipt.json`. Deploy then
injects the verified root, tree digest, and receipt digest. Setting
`TERRASCALE_REQUIRE_MSQUIC=1` without that verified dependency fails before
test discovery, so the required lane cannot reach Cargo.

The published Deploy CLI `1.0.80` does not contain this candidate contract.
Publication of a newer reviewed CLI, immutable cross-run transfer, a
digest-pinned static builder with its Rust, Cargo, C compiler, linker, and
environment recorded in the receipt, an exact consumer-image self-check,
loader-visible MsQuic for the .NET qualification process, and protected
qualification evidence remain external blockers. Local package output and the
ignored binding records do not clear any of them. Dynamic GNU diagnostics,
including binaries with Nix-store interpreters or runtime paths, cannot be
packaged under this contract.
