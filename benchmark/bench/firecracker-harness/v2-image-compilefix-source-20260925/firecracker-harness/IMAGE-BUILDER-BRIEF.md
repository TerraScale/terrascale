# Reproducible Firecracker image fixture: bounded implementation brief

The current `pins.toml` identifies a historical Linux 6.1.155+ kernel and
Ubuntu 24.04.3 root filesystem by SHA-256, but their upstream recipe and
signatures are unknown. Keep those pins and their default harness behavior
unchanged. Build a **separate** `terrabase-noble-podman-v1` candidate that can
be verified from a clean checkout before any RustFS, Von, or multi-node VM gate
is claimed. This brief assigns only the image builder source and its versioned
recipe; it does not authorize a multi-GB image build or fixture qualification.

## Bounded ownership and interface

Implement a Rust-only crate in `firecracker-harness/crates/fc-image-builder/`
and a versioned recipe under
`firecracker-harness/images/terrabase-noble-podman-v1/`. A future profile
integration owner will edit the existing `fc-harness` controller, workspace and
root Makefiles, and run manifest to select the new image lock explicitly.
Do not edit `pins.toml`, the existing controller/guest-agent/network source,
`AGENTS.md`, or the current benchmark table. Output fixture artifacts and
receipts may go only under `bench/firecracker-harness/image-fixtures/<identity>/`
or a caller-supplied output path, never under the TerraBase source repository.

The builder should accept a versioned lock and an explicit input/cache/output
directory, then emit a machine-readable provenance receipt containing: schema
and recipe version; exact source URLs, immutable source revisions and detached
signature/key fingerprints; source-content hashes; the snapshot timestamp and
all installed package names, versions and `.deb` SHA-256 values; kernel config
hash and build toolchain identity; rootfs construction commands; produced
kernel/rootfs paths, sizes, SHA-256 values; and the OCI image digests loaded in
the guest. Refuse missing or mismatched input identities. A clean repeat build
must either produce the same bytes or explicitly record controlled
non-reproducibility with the differing metadata; do not label a hash-only local
artifact as reproducible.

## Candidate source facts requiring full lock closure

The dependency research packet identified these **candidate**, not yet
qualified, inputs:

| Role | Candidate identity | Verification gate |
| --- | --- | --- |
| Firecracker CI kernel lineage | Amazon Linux annotated tag `microvm-kernel-6.1.155-13.282.amzn2023`, peeled commit `069c93d2b7bddda87c472453c38c450faf86f999` | Verify tag/signature and source commit, freeze the exact Firecracker-compatible config and build environment. Firecracker's CI config is not guaranteed for an arbitrary upstream 6.1 kernel. |
| Ubuntu rootfs base | Ubuntu 24.04.5 amd64, SHA-256 `e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86` | Verify canonical signed `SHA256SUMS` and key chain before extracting; do not rely on the literal hash alone. |
| Package universe | Noble snapshot `20260910T164400Z` | Verify the signed `InRelease` and freeze the complete package dependency closure, including `.deb` hashes and repository component/architecture. |
| Guest container runtime | Podman `4.9.3+ds1-1ubuntu0.2`, crun `1.14.1-1`, conmon `2.1.10+ds1-1build2` | Verify every package and dependency against the locked snapshot; record runtime versions inside the built guest. |

The complete package closure, trusted key fingerprints, image layout, kernel
config hash, build toolchain/container digest, download mirrors, exact
signature-verification commands, and failure-injection cases must be imported
from the full requirements packet before coding the fetch/build path. Treat
this table as a scoped starting point, not an approved lock file. A service OCI
tag is not an identity: RustFS and Von inputs need immutable content digests.
The image builder must never reach into current host containers or substitute a
host service for a VM fixture.
The unprivileged controller's lack of effective `CAP_NET_ADMIN` is not proof
that the environment cannot run the separate isolated network smoke: its
authorized operator may explicitly acquire that capability through the
existing passwordless `sudo` path. The image builder itself must never use
that privilege or mutate host networking.

## Accepted split builder model and revised recipe contract

Use **two separately pinned image roles**. The existing `docker.io/library/rust:1.98.1-bookworm` amd64 child digest `sha256:c49256cbe5ea0188bc658a689500d70c41eb51f009a7a7be209caf60a944f3ec` is only the Rust CLI **compilation** environment. Build `fc-image-builder` from the exact source and frozen Cargo.lock there, capture Rust/Cargo versions, image child/index digests, dependency lock and produced executable hash, and run that executable on the host only after recording its host ELF/runtime compatibility. It orchestrates bounded, rootless, explicitly pinned Podman commands; it does not install or resolve Ubuntu `.deb` packages inside Bookworm. The host Podman executable path, SHA-256/version, rootless identity, command argv, mounts, network mode and resource limits belong in each command/provenance receipt. No host test or benchmark is implied by running the builder CLI.

The **only** environment for Canonical `gpgv`, Noble APT 2.8.3 solving/installing, kernel build tools, `mke2fs` and `e2fsck` is a derived Ubuntu Noble builder rooted at the pinned amd64 `docker.io/library/ubuntu` child digest `sha256:496754492fb28b4d3049432f2ca787449331e23fb14f0dd3fffea86bf5a93eb4`; the inspected `ubuntu:24.04` multi-arch index descriptor is `sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3`. Record and verify both index and child/platform identity; registry metadata inspection did not fetch the layer or prove installed apt/gpgv bytes. The research packet's 175 `builder` packages belong to this Noble derived image; the separate 126 `guest` packages populate the Ubuntu Base rootfs. Keep independent locks, package inventories and receipts for both groups. The guest closure cannot be treated as the builder closure, and neither may be silently satisfied by Debian packages already present in the Rust CLI image. Do not copy the CLI into Noble or assume cross-distribution ELF compatibility; the host orchestrates the child through Podman until a separately reviewed transfer/ABI closure exists.

The next recipe version must replace the ambiguous single `[builder]` OS identity with `[rust_cli_builder]` and `[ubuntu_build_builder]`. The former pins the Rust image's amd64 child/index digest, exact CLI source/Cargo.lock/toolchain identity and output executable hash. The latter pins the Ubuntu amd64 child digest, Noble snapshot/suites/components/architecture, apt 2.8.3 settings, signed index/keyring identities, a distinct complete `builder-packages.lock.json`, exact tool executable hashes, derived OCI digest and offline execution limits. Keep `packages.lock.json` as the **guest** package lock, with its own solver receipt. Its current `toolchain_lock_path` cannot remain an untyped mixture of Debian and Ubuntu tools: split Rust CLI compiler provenance from Noble kernel/ext4/gpgv/APT tool provenance. The receipt binds both source image digests, the final Ubuntu derived digest, the two locks and solver transcripts, every command and output hash. Changing recipe bytes changes its derived ext4 UUID and requires a new recipe hash; preserve the old candidate and receipts as historical inputs rather than relabeling them.

For each distinct Noble lock, require `schema_version`, `qualification_status="apt-solver-verified"`, snapshot timestamp, apt version and exact settings/architecture, six per-suite/component signed-index receipts (trusted-key source and full signer fingerprint, InRelease and Packages identities/hashes), `apt-get -s` command argv and stdout/stderr hashes, resolved `Depends`/`Pre-Depends` edges, and every selected `.deb` name/version/architecture/suite/component/URL/size/SHA-256. `packages.lock.json` names only the guest closure; `builder-packages.lock.json` names only the builder closure and additionally records the derived Ubuntu image digest and installed gpgv/apt/dpkg/compiler/kernel/e2fsprogs paths, versions, SHA-256 values and build argv. The recipe pins each final lock-file hash plus the host Podman executable path/version/SHA-256 and rootless resource limits; a different host binary requires a different recipe identity. Fill the derived digest only after the controlled builder bootstrap, and never embed the final recipe/lock hash inside the derived image in a way that would create a digest cycle. These are qualification fields, not claims that the current candidate file already contains them.

**Bootstrap order and fail-closed gate:** a separately reviewed builder-bootstrap step may inspect the pinned Ubuntu base, verify Canonical/Ubuntu signed metadata and run `apt-get -s` with the exact Noble snapshot/settings to finish the 175-package closure; only verified `.deb` bytes may produce the derived Ubuntu builder and its digest. Its initial gpgv trust root must come from the pinned Ubuntu child or a separately preauthenticated operator input; the snapshot's keyring `.deb` cannot be its own proof before that `.deb` has been verified against a signed index. Record which initial trusted bytes and fingerprint were used. The main `fc-image-builder fetch/build` path remains disabled until that signed solver receipt, complete builder and guest locks, toolchain/container closure and derived digest are recorded and checked. This avoids circularly requiring a derived image digest before the controlled bootstrap that creates it, while preventing an unverified candidate packet from authorizing a normal fetch/build. The current `candidate-package-research.json` is a graph candidate, not an apt solver receipt; `recipe.toml` still names Bookworm in its sole `[builder]`, has an empty derived digest, and is not ready. No container, package fetch, image build or guest qualification was run to make this decision.

Source identity for this split decision, under `ts-benchmark` HEAD `5685f163bb7e9339e8eb0c839fe2512aaf562648`: current `recipe.toml` SHA-256 `e1fa94b01f56f37f8f3e80253076bfd379c7b0264f7b04c83fd76c550b10ee37`; research packet `00a430ce9dc7e793cf05a81581ffdc75dab495ee74cbd5c21f9e2d567af3632d`; `fc-image-builder/src/recipe.rs` `96ac46655fea83266b4908530f95a0bdb9944ef70c10140e4da3d8208d8f0a00`, `main.rs` `17e47ecd63dbe82706ab29a127e405b3b11f53f23f7b16c273944a4c6f711841`, and `tool.rs` `c9801b96fa1ec748b934375ee5ff7e84802b0b9580687dd95f2b4e109483cfbc`. These are moving, uncommitted working-tree observations, not verified package or image artifacts.

## Implementation and verification contract

Separate fetch/verify/build/inspect phases so signature and checksum failure
stops before running untrusted input. Use popular maintained Rust crates for
structured formats and hashing where they fit; external build tools may run
only as explicit, versioned recipe steps with their stdout/stderr and exit
status captured. Build commands use the repository Makefile with low CPU and
I/O priority. No tests or benchmarks execute on the host: compile any test
program with `--no-run`, then run it inside the pinned four-vCPU, 8192-MiB,
swap-off Firecracker guest through the harness. Keep raw image and container
bytes out of Git; small JSON receipts and Markdown result tables remain
reviewable in `bench/`.

Acceptance requires a read-only recipe/security review before any large fetch
or image build; a verified signed-source fetch; a reproducible output or an
honest nondeterminism receipt; read-only inspection that the guest image has
the intended kernel, Podman/crun/conmon and network setup; then a separate
explicit profile integration and a real Firecracker guest caller. An image
builder receipt alone cannot qualify RustFS, Von, or replication.

Source anchors: `pins.toml`, `crates/fc-harness/src/main.rs` (`pin_string`,
`initial_manifest`, `run_vm_inner`), `Makefile`, `NETWORK-FIXTURE-BRIEF.md`,
and `bench/terrabase-rust-acceptance-v1.md`.

## Full source packet and exact immutable candidate links

The requirements packet was delivered read-only on 2026-09-24 against
`ts-benchmark` HEAD `5685f163bb7e9339e8eb0c839fe2512aaf562648`.
It verified the Firecracker config source hash and registry metadata only;
it did not fetch image layers or verify the Ubuntu tar signature because
`gpgv` was absent in its inspected host. The builder must perform these checks
in an environment with a pinned verifier.

- Amazon Linux kernel source:
  `https://github.com/amazonlinux/linux.git`, annotated tag
  `microvm-kernel-6.1.155-13.282.amzn2023`, peeled commit
  `069c93d2b7bddda87c472453c38c450faf86f999`. Firecracker v1.15.1
  source commit `f82c0bd0f0a74015642a0d452880f3ad10147b14` and x86_64
  6.1 config URL
  `https://raw.githubusercontent.com/firecracker-microvm/firecracker/v1.15.1/resources/guest_configs/microvm-kernel-ci-x86_64-6.1.config`,
  SHA-256 `391d8d498f89e56b391be5ea4a7501ebc44e6d094331ecaabdefed1da6b28a96`.
  Firecracker kernel-policy source SHA-256
  `f6d05719c129d8370b581ddc838de2bd113d1f35127f4da4236853ca9aa6f2fb`.
- Canonical Ubuntu Base 24.04.5 amd64 tar:
  `https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/ubuntu-base-24.04.5-base-amd64.tar.gz`,
  SHA-256 `e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86`.
  Verify adjacent `SHA256SUMS.gpg` over `SHA256SUMS` and assert the full
  Ubuntu CD Image primary signing fingerprint
  `843938DF228D22F7B3742BC0D94AA3F0EFE21092` from `VALIDSIG` before
  extraction. HTTPS and a literal hash alone are insufficient.
- Ubuntu package universe:
  `https://snapshot.ubuntu.com/ubuntu/20260910T164400Z/`; freeze only
  explicitly selected Noble/Noble-updates/Noble-security components.
  Noble-updates `InRelease` SHA-256
  `9f03de549c67af423447b5204dcd46298d23f69fe22bf30c76e9ee01548d0246`;
  the inspected 1,690,332-byte package index SHA-256
  `cf06d0b20daa7eea4d4a4f4abc0fd8b29aed8389367681c6f6653c5068155265`
  matched that signed index. Podman `4.9.3+ds1-1ubuntu0.2` `.deb` SHA-256
  `e5c1c37e387ed14c352a744a75fbb79fb2f82573ca7bf36886e3b7333fc9ef1a`;
  crun `1.14.1-1` and conmon `2.1.10+ds1-1build2` still need their full
  hashes and every transitive package lock entry.
- Pinned amd64 Rust CLI compilation base (not the Noble APT/kernel/ext4 builder):
  `docker.io/library/rust@sha256:c49256cbe5ea0188bc658a689500d70c41eb51f009a7a7be209caf60a944f3ec`,
  child of multi-arch index
  `sha256:93ce27a88655056a51dbdd8f5f2d7ddc071c7b0070fb288a37b5a285fc83971e`.
  The old tag is `1.98.1-bookworm`, but the digest must select the bytes.
  Alternative Ubuntu amd64 child is
  `docker.io/library/ubuntu@sha256:496754492fb28b4d3049432f2ca787449331e23fb14f0dd3fffea86bf5a93eb4`.
  Pin any added compiler/build packages and record the derived builder digest.
- Optional offline OCI smoke image:
  `docker.io/library/busybox@sha256:dc88b80842580654294472735332ec7c7789a4010ec9a68b69f9b88c19abede6`
  (amd64 child). Copy by digest to an OCI archive, hash the archive, and run
  with guest networking disabled. Service OCI digests are separate gates.

### Required Rust CLI phases

`resolve/fetch` accepts only locked HTTPS origins into a unique run-owned
cache, verifies source commit, Canonical signature/fingerprint, signed apt
InRelease/index digests, and every `.deb` hash before extraction or install.
`lock` writes a complete transitive `Depends`/`Pre-Depends` package lock with
name, version, architecture, repository component, `.deb` SHA-256, apt version
and settings, keyring/signer and signed index hashes; fail when unavailable at
the snapshot timestamp. `build` consumes verified local inputs offline,
disables network in the rootless run-owned Podman build container, sets CPU,
memory, PID and disk bounds, and emits kernel/ext4 plus a provenance manifest.
`verify` recomputes all output hashes, package inventory, filesystem metadata
and `e2fsck` status. No `sudo`, privileged container, `--network=host`,
host bridge/TAP/ns, KVM or VM operation belongs inside this crate. Invoke
pinned external tools through `std::process::Command` with captured
stdout/stderr/exit; add no shell scripts or unrestricted shell text.

Set `SOURCE_DATE_EPOCH` from the locked source epoch and freeze kernel
`KBUILD_BUILD_TIMESTAMP`, user/host, `LOCALVERSION`, `BUILD_SALT`, locale,
timezone and compiler/toolchain versions. Assert the final kernel `.config`
hash and critical Firecracker settings. For ext4, pin byte size, block/inode
sizes, reserved percentage, label, UUID and hash seed. Use pinned `mke2fs`
with `-U <fixed UUID>`, `-L <fixed label>`,
`-E hash_seed=<UUID>,root_owner=0:0,lazy_itable_init=0,lazy_journal_init=0`,
and `E2FSPROGS_FAKE_TIME=<SOURCE_DATE_EPOCH>`; normalize file mtimes, record
uid/gid/mode/xattr/hardlink inventory, preserve dpkg records, and clear only
listed volatile machine-id/random-seed/log/package-cache files. Record
superblock metadata and clean `e2fsck`. Do not claim byte reproducibility
until two clean isolated builds produce equal `vmlinux` and ext4 hashes.

The output manifest must include bench source HEAD/dirty identity, recipe and
package-lock SHA, exact kernel commit/config/toolchain closure, Ubuntu tar
signature/fingerprint, apt snapshot/index/package provenance, ext4 settings
and normalized root-tree inventory, builder base and derived OCI digests,
output paths/lengths/hashes, commands, timings, verification receipts and
limitations. Keep time-varying timestamps in the sidecar, not the ext4 bytes.

### Decisions and follow-on acceptance

These remain explicit locks to close, not assumptions: ext4 capacity and
reserved blocks (historical 2,560 MiB may be too small for Podman layers),
full package closure and crun/conmon hashes, compiler/build-tool closure and
derived builder digest, whether to use the BusyBox offline smoke image, and
registration of the new fixture output in root cleanup/fixture inventory.
The profile integration owner separately adds a versioned image selector and
new receipt provenance without weakening `pins.toml`; current `include_str!`
legacy checks reject alternate kernel/rootfs bytes even when paths are passed.

After a reviewed two-build recipe, a separate authorized VM operator must
boot the candidate as one 4-vCPU, 8192-MiB, SMT-off, swap-free guest, verify
effective config and guest CPU/RAM/swap, ext4 UUID and cgroup v2/systemd PID1,
Podman/crun versions, offline BusyBox `podman run --rm --network=none <digest>
true`, writable overlay cleanup, guest manifest, and teardown. A four-role
topology needs at least 32 GiB guest memory before host overhead; the earlier
21,078,048,768-byte host-available receipt cannot qualify it. Save real guest
receipts and benchmark tables under `bench/`, never TerraBase source.

## v2 kernel.org candidate addendum

`terrabase-noble-podman-v2` is a separate unqualified candidate. It preserves
the v1 recipe and default harness profile as historical inputs. The kernel
source is the signed kernel.org Linux 6.18.53 tarball, SHA-256
`4d6fba95c2244b08a7b4144a4d38b9be4fb31abb5e7682ae40bb5cb11374cfe0`; verify
the detached signature over the decompressed tar using Greg KH fingerprint
`647F28654894E3BD457199BE38DBBDC86092693E` before extraction. Use the pinned
release epoch `1789996005` / `2026-09-21T13:06:45Z`. Firecracker 1.17.0 source
policy and the 6.18 config seed are pinned to commit
`95f868c8e345b1cc8faccd1a3c910b4989dc3f58`. The bootstrap must run
`make olddefconfig`, record the resolved `.config` and its diff, and leave
both in `generated-pending-independent-review` until an independent reviewer
accepts the settings. It must not silently treat the Amazon Linux CI config as
qualified for the upstream kernel.

The official Firecracker 1.17.0 x86_64 release archive is pinned by size and
SHA-256 from GitHub release asset metadata. Its tag is unsigned, so receipts
must describe the archive as an observed release asset and must not claim a
source build or release signature. Only this fetch may accept one HTTP 302
from the exact pinned GitHub URL to HTTPS `release-assets.githubusercontent.com`
on port 443/default, followed by HTTP 200, exact byte length, and the pinned
archive hash. Record initial and final URLs and redirect count; keep the global
HTTPS fetcher redirect-disabled. The parser must extract only the reviewed
member path and record its observed byte hash.

The v2 bootstrap uses the same reviewed Noble package closure and derived
builder process as v1, then emits separate config and Firecracker evidence in
its candidate proposal and qualification receipt. A `candidate-observed`
executable hash and generated config are not review approvals. Normal Fetch or
Build stays disabled until independent review closes the config and release
pins and all split builder, package-lock, and Rust CLI compilation pins are
complete. No profile default changes as part of this addendum.
