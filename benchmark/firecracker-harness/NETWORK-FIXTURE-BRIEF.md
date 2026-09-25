# Next Firecracker fixture gate: networked services

This is an implementation brief, not a qualified benchmark topology. The
current controller starts one NIC-less VM, launches one payload through vsock,
and deletes its writable rootfs after the receipt. The successful smoke at
bench/firecracker-harness/vm-smoke-1790244314765-2201660-pass verifies that
path and the 4-vCPU/8,192-MiB machine configuration. It does not exercise a
remote service. The acceptance matrix requires separate matching VMs for each
database node, RustFS, Von, and the load generator when used
(bench/terrabase-rust-acceptance-v1.md, environment gate).

Read-only host inventory on 2026-09-24 found /dev/net/tun present and
passwordless sudo available, but the current process has no effective
capabilities. There are no run-owned TAPs, bridges, or named network namespaces.
Rootless Podman runs unrelated host workloads; those are not guest fixtures.
Read-only inspection of the pinned Ubuntu rootfs found no Podman, Docker,
containerd, crun, runc, nerdctl, or LXC launcher in /usr/bin. The new image
recipe must add a pinned guest container runtime before claiming container
tests. No network resource was created by this inventory.

## Inputs to make reproducible first

pins.toml checks the exact historical kernel and Ubuntu rootfs SHA-256 values.
The only provenance record, /tmp/ts-oci-readiness-20260921/vm-inputs-v1/MANIFEST.txt,
contains hashes and prior source heads but no upstream artifact URL, signature,
package snapshot, kernel config, or image-build recipe. Treat these bytes as a
working local smoke input, not a reproducible clean image. Before a networked
qualification, freeze a versioned image recipe with a trusted upstream kernel
source/release and config, rootfs distribution snapshot and package versions,
container runtime and guest service configuration, build commands, resulting
image hashes, and verification of the built bytes. Preserve the old pin for
historical receipts; use a distinct new image identity for new trials. Pin each
RustFS container by immutable OCI digest and each Von service/model artifact
by source and content digest. A tag alone is not an identity. The current
rootfs must not be silently replaced under the existing pin.

## Proposed run-owned topology and lifecycle

Firecracker v1.15.1 supports one host TAP backend per guest NIC through
PUT /network-interfaces/{iface_id}; its network setup guide documents the
host_dev_name/guest_mac fields and guest-side address setup. Provision all TAPs
and a bridge inside one private network namespace owned by one run supervisor.
Give each VM a distinct TAP, MAC, static IP, rootfs copy, Firecracker process,
API/vsock socket directory, and evidence directory. Keep the supervisor outside
the namespace and launch its Firecracker children inside it, so local Unix
API/vsock sockets remain usable without host routing or NAT. No host uplink is
needed for a first database-to-RustFS/Von fixture if images are preloaded and
the guests can reach one another on that isolated bridge. Resolve the actual
guest interface name and verify routes and peer connectivity from inside each
guest; Firecracker does not guarantee NIC enumeration order. This requires a
runtime with scoped CAP_NET_ADMIN and named-network-namespace support. If the
environment lacks that capability, stop at preflight and report the missing
capability; do not mutate global forwarding, firewall, or host service routing.

Use one supervisor as the sole owner of the namespace, bridge, TAPs, child
processes, staged images and sockets. Reserve unique run-scoped names, persist
a small ownership record before each allocation, and refuse to adopt/delete
resources whose identity does not match that record. Apply the existing
4-vCPU/8,192-MiB/swap-off checks to every VM, assign nonoverlapping host CPU
sets, and account for the sum of all guest memory plus host headroom before
launch. The latest passing smoke recorded 21,078,048,768 host-available bytes;
four 8-GiB VMs would need at least 34,359,738,368 bytes before host headroom,
so that receipt cannot qualify the full four-role topology. Host swap was
active in that smoke and must not be counted as guest capacity.

Startup order: verify all pinned artifacts and resource capacity; allocate the
private network; start service VMs; verify their own guest resource reports and
container readiness; then start database and load-generator VMs. Record the
effective Firecracker machine/NIC configuration, host CPU assignment, namespace
and TAP identity, guest IP/routes, image/container digests, service health,
network error counters, and per-VM/container resource usage in receipts. The
guest manifest must identify the network actually observed, not merely the
planned addresses. There is no host-service fallback.

On normal completion or any setup, timeout, payload, or service failure, stop
guest workloads and containers, stop and reap every Firecracker child, remove
only this run's TAPs/bridge/namespace, and remove temporary rootfs copies and
socket directories. Retain failed receipts, logs, and bounded guest archives.
If the supervisor itself crashes, a later explicit cleanup command may use
the ownership record to reconcile stale resources after proving no matching
process still owns them. Do not use broad interface-name or namespace sweeps.
Test failures after each allocation step and a forced supervisor termination
before treating cleanup as reliable.

## Real caller gate

crates/tb-object/src/client.rs currently has generic ObjectClient capability
probes for conditional create, exact range reads, multipart abort, and stale
conditional update. Its production RustFS/S3 endpoint wiring and a real
process-level caller are still absent. Its publish_manifest method explicitly
returns UnsupportedGuarantee even after the conditional-update probe, because
fenced manifest authority is not integrated. The first remote-service test
must therefore run the real Rust object-store client inside a database VM
against a digest-pinned RustFS container inside a separate 4-vCPU/8-GiB VM.
Probe create-only races, range bytes and metadata, multipart abort, and stale
conditional update; then exercise immutable upload/readback and corrupted or
missing range rejection with the real client. Record provider/container digests,
request/response outcomes, retries, timeouts, bytes, and resource counters.
Keep object-durable acknowledgment and SSD-loss recovery unsupported until a
fenced publication authority and cold-recovery caller pass their own gates.

Von must likewise run in its own matching VM with a pinned service/model
artifact and an actual query-advisor caller. A healthy container alone does
not qualify Q02. Multi-node replication needs separate database VMs and the
real gRPC/LZ4 peer caller, not an in-process merge test. Run all unit and
integration tests inside the guest; host builds and preflight are preparation.

Source anchors: crates/fc-harness/src/main.rs
(run_vm_inner, connect_guest_vsock), crates/fc-guest-agent/src/main.rs
(execute_payload), ../../terrabase/crates/tb-object/src/client.rs
(probe_capabilities, publish_manifest), ../../terrabase/docs/engineering-plan.md
(object-tier gate), and the Firecracker v1.15.1 network setup guide at
https://github.com/firecracker-microvm/firecracker/blob/v1.15.1/docs/network-setup.md.
