# Firecracker test harness

This crate launches TerraBase workload executables in a Firecracker guest. It
does not run the workload on the host when Firecracker, KVM, the pinned images,
or the guest resource checks are unavailable.

The initial local image lock is in `pins.toml`:

| Artifact | Pin | Local source |
| --- | --- | --- |
| Firecracker | v1.15.1 | `firecracker` on `PATH` |
| Guest kernel | SHA-256 `e20e46d0c36c55c0d1014eb20576171b3f3d922260d9f792017aeff53af3d4f2`, Linux 6.1.155+ | `/tmp/vm-inputs/vmlinux` |
| Guest root filesystem | SHA-256 `dd49aee1d9c28f8e781a627bec744db3a961ce4402950130ff9268504c551314`, Ubuntu 24.04.3 | `/tmp/vm-inputs/rootfs.ext4` |

The kernel and root filesystem hashes were recorded in
`/tmp/ts-oci-readiness-20260921/vm-inputs-v1/MANIFEST.txt`. That manifest does
not identify the upstream artifact URLs or build recipe. The harness verifies
the exact local bytes and reports their upstream provenance as unqualified; it
never downloads or silently substitutes another image.

A verified candidate can instead be selected with `--image-profile
IMAGE-MANIFEST.json` on `preflight` or `run`, or with `image-smoke
IMAGE-MANIFEST.json`. The matching Makefile variables are `IMAGE_PROFILE` for
`guest-run` and `guest-image-smoke`. The controller checks the candidate recipe,
signed source and package receipts, relative artifact paths, sizes, and hashes
before selecting its kernel and root filesystem. The v1 recipe is retained
as an archive-only input and is not accepted by the current image builder. The
separate v2 candidate under `images/terrabase-noble-podman-v2/` remains
unqualified; no image is qualified until its builder emits a verified manifest
and a guest smoke passes. Without an explicit profile, the historical pins
above remain the selected images.

Build the host controller and static musl guest agent through the Makefile.
`make -C firecracker-harness test-build` compiles a static musl test executable
with `--no-run`. Pass its printed path as `TEST_BINARY` and a unique `RUN_ID` to
`make -C firecracker-harness test-vm`; the `test` alias runs that same Firecracker
target. `test-build-image-builder` compiles the image-builder library tests for
musl without executing them; pass its test executable to the same `test-vm`
target. Bare `make -C firecracker-harness` prints the available targets. The
host never runs harness tests as acceptance evidence. Set
`HARNESS_BINARY` and `GUEST_AGENT_BINARY` to frozen copies when verifying an
immutable artifact set. Direct `fc-harness run` calls require
`--benchmark-repo /absolute/path/to/ts-benchmark`; the receipt records that
runtime checkout, while the controller executable hash identifies the frozen
build. Smoke commands use `FC_BENCHMARK_REPO` or discover the checkout from the
current directory. Default run output lives under that checkout's
`bench/firecracker-harness/` directory.

```sh
make -C firecracker-harness build
make -C firecracker-harness preflight
make -C firecracker-harness guest-smoke
```

Networking is opt-in. `make -C firecracker-harness guest-network-smoke` boots one
guest with a private, run-owned network namespace, bridge, and TAP, and verifies
that the guest observed its configured static IPv4 address. The bridge has no
host uplink, NAT, or external service. The normal `guest-smoke` and `guest-run`
commands remain NIC-less. A measured run may request the isolated NIC with
`./firecracker-harness/target/release/fc-harness run --benchmark-repo /absolute/path/to/ts-benchmark ... --network-mode isolated --`.
This is a lifecycle smoke, not RustFS, Von, or multi-node qualification.

Each isolated run keeps `network-ownership.json` and `vmm-process.json` in its
output directory. The launcher writes the VMM PID, process start time, and
namespace inode before starting Firecracker. After an interrupted run, use
`make -C firecracker-harness network-cleanup RECEIPT=/absolute/path/to/network-ownership.json`.
The run also keeps `network-helper.bin`, a hash-checked copy of the exact
controller executable used for network launch and privileged signaling. The
schema-2 ownership receipt binds its path and SHA-256; cleanup verifies those
bytes again and removes the helper only after the namespace is reconciled.
Cleanup signals only a VMM matching its recorded identity. It refuses a changed
namespace or an unknown process, leaving the receipts for manual reconciliation.
For a controlled crash-recovery test, set `FC_HARNESS_TEST_EXIT_AFTER_NETNS_ADD=1`
on an isolated run. The controller exits with code 91 after namespace creation
but before recording its inode; `cleanup-network` then removes only the empty
namespace named by the durable planned receipt. Never use this flag on a
measured run.
This pre-inode recovery trusts the receipt's unpredictable 128-bit namespace
name as its ownership token, plus an empty process list, loopback-only links,
and a stable inode recheck. Namespace creation and inode persistence are not
atomic; if any check differs, cleanup stops for manual reconciliation.

The reviewed opt-in lifecycle was exercised with frozen host binary SHA-256
`5684795c8e5b573a30e0af0ef5af6f76dec7232752734783db92d0f3fedc541f`
and guest agent SHA-256
`03ee13edbcddcba51f99b8c1bf7edfaea5128452ce9c227481154e9d036d60af`.
The five harness tests passed inside the NIC-less four-CPU guest at
`bench/firecracker-harness/fc-harness-network-unit-final-20260924-a/`.
`bench/firecracker-harness/vm-network-smoke-1790248794835-2793373/` records
guest `10.77.0.2/24`, four CPUs, no swap, exit 0, and cleaned network
ownership. The intentional after-add exit-91 run at
`bench/firecracker-harness/vm-network-smoke-1790248886336-2800482/` records
the planned namespace with no processes or extra links before cleanup, then
`status=cleaned` and no run-owned namespace, bridge, TAP or VMM afterward.
Small build identity and logs are under
`bench/firecracker-harness/harness-final-98e41821e42a30eacc568246a25d32c3bb4433e74df4f347cadf96cc71a53d3c/`,
`bench/firecracker-harness/network-smoke-final-20260924.log`, and
`bench/firecracker-harness/network-fault-final-20260924.log`. These checks
verify the single-guest network lifecycle, not a service topology or image
provenance.

`preflight` opens `/dev/kvm` and checks `KVM_GET_API_VERSION`, Firecracker's
version, both image checksums, the root filesystem's swap configuration, host
CPU affinity, available memory, and the guest agent. `guest-smoke` launches two
fresh guests: `/usr/bin/uname` must pass and `/usr/bin/false` must fail with
exit code 1 recorded in a failed receipt. Each guest must report four online
CPUs, at least 7 GiB of guest-visible RAM, and an empty `/proc/swaps` before a
command can run.

Run a measured executable with a unique run id. The binary must be executable
inside Ubuntu 24.04; static musl binaries are suitable for this root filesystem.
For a process test that needs a separate guest executable, repeat `--payload-file
HOST_PATH:BASENAME` on the controller command (at most four files, 128 MiB
combined). The Makefile `guest-run` target accepts one `PAYLOAD_FILE` value.
Files must be regular executables; the guest receives them only under
`/root/fc-harness-payloads/BASENAME`, while the main program remains
`/root/fc-harness-payload`. A process test can therefore invoke
`/root/fc-harness-payloads/tb-server`. The controller stages each input from an
opened file into a private bounded snapshot, verifies the copy inside ext4,
and the guest agent checks its mode and hash before launching the main program.

```sh
make -C firecracker-harness guest-run \
  SUBJECT_REPO=/absolute/path/to/terrabase \
  PROGRAM=/absolute/path/to/guest-compatible-driver \
  RUN_ID=terrabase-point-smoke-unique \
  TIMEOUT_SECONDS=30 \
  ARGS='run --case point-hit-random --rows 1024 --operations 16 --body-bytes 256 --seed 1 --trial 1 --database /root/fc-bench-data/point-hit-1.sqlite --samples /root/fc-harness-output/point-hit-1.jsonl --receipt /root/fc-harness-output/point-hit-1.json --guest-manifest /root/fc-harness-output/guest-manifest.json --benchmark-revision BENCHMARK_HEAD --engine-revision TERRABASE_HEAD'
```

Replace BENCHMARK_HEAD and TERRABASE_HEAD with the actual Git revisions before
running the example.

Each run gets a new output directory under `bench/firecracker-harness/<run-id>`
and a private writable copy of the pinned root filesystem. The source images are
never modified. Firecracker is pinned to four host CPUs selected from the
calling process's allowed affinity set, with `vcpu_count=4`, `mem_size_mib=8192`,
`smt=false`, and no balloon device. API and vsock sockets live in a private,
short temporary directory to satisfy the Unix socket path limit. The controller
opens the base vsock Unix socket and sends the Firecracker host-initiated
connection handshake for the guest agent port. The controller
reads back the effective machine configuration before starting the VM and rejects
any CPU, memory, or SMT mismatch. A guest agent communicates over vsock, captures
the guest configuration, writes the guest manifest in the output directory
before launching the executable, and
returns its exit status and a compressed archive of the guest output directory.
The manifest combines guest-observed CPU count, memory, and swap state with
the verified machine configuration, image hashes, and source identities. The
controller also records its own executable, Firecracker, the guest agent, and
the kernel from frozen launch snapshots. The host receipt records the writable
root filesystem hash after guest-file injection and before boot; subsequent
guest writes naturally change that disk. The guest manifest carries the
controller, Firecracker, agent, and workload digests passed in the verified
launch request. The
agent creates /root/fc-bench-data for database files; that directory is outside
the 512-MiB output archive.
The controller stops the VM and removes the temporary writable root filesystem
after the guest exits; logs, output archive, and the manifest remain.
The repository tracks small receipts, VM logs, and fixture hash indexes for
review. The generated guest image copy, compiled engine test executables,
process ID files, and compressed guest output archives stay local and are
ignored by Git; inspect an archive locally and copy qualifying results into
the Markdown benchmark tables.
`TIMEOUT_SECONDS` sets a wall-clock limit for the guest workload (default 1800,
maximum 3500). On expiry the guest agent kills and reaps the workload, archives
its stdout/stderr files, and returns a timeout result. The controller writes a
failed receipt and stops/reaps Firecracker. Do not wrap the controller in a host
`timeout` command, which could leave a guest process behind.

The receipt records the benchmark and subject Git revisions, dirty state,
tracked diff and untracked-file hashes, executable hash, Firecracker version,
KVM API, guest image hashes, host CPU affinity, guest-observed resources,
host swap state, filesystem, run status, and exit code. Host page cache is not
flushed. The host CPUs are affinity-pinned but not exclusively reserved; report
that contention when interpreting timing. A host swap setting does not qualify
the guest: `/proc/swaps` is checked inside every VM.

The default runner has no guest NIC. It supports tests whose
required services are embedded in the executable or do not require network
access. RustFS, Von, multi-node replication, and external load generators must
each be provisioned in their own matching Firecracker guests with a qualified
network topology before those integration gates can pass. A host RustFS/Von
process cannot satisfy those gates.

`make -C firecracker-harness guest-rustfs-fixture` is the only networked
service fixture. It boots two guests on one private, run-owned network: a
RustFS service guest from the receipt-verified, OCI-digest-pinned artifact
named by `RUSTFS_ARTIFACT_RECEIPT`, and a client guest running
`CLIENT_PROGRAM` (the real TerraBase Slow S3 integration executable). It also
requires `SUBJECT_REPO`, a verified `IMAGE_PROFILE`, and a unique `RUN_ID`, and
checks host memory for both guests before staging and again before boot. There
is no host S3 endpoint. After an interrupted run, reconcile only the recorded
resources with
`make -C firecracker-harness rustfs-fixture-cleanup RECEIPT=/absolute/path/to/pair-network-ownership.json`.
Von, multi-node replication, and external load-generator fixtures do not exist
yet.

All harness source and evidence stay in `../ts-benchmark`; do not put benchmark
data or results in the TerraBase source repository.
