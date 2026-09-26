# Fixture register

Each fixture gets a stable directory with its generator revision, fixed seed,
record/key distribution, document size and schema, expected result digest,
storage-tier setup, and dataset hash. Use the same logical fixture for TerraBase
and SQLite. Record any intentional fixture revision before measuring a candidate.
No new acceptance fixture has been published here yet.

Versioned Firecracker guest images belong under
`bench/fixtures/firecracker-images/<profile>/<image-build-id>/`. The directory
is a protected fixture output: do not prune it as a transient benchmark run.
Keep small recipe, package-lock, provenance, artifact-index and verification
receipts visible; generated `vmlinux`, `rootfs.ext4`, download caches and OCI
archives remain ignored. The historical local-checksum-pinned image is not
silently replaced by a new profile. The current candidate profile is
documented in
[`../../firecracker-harness/images/terrabase-noble-podman-v2/README.md`](../../firecracker-harness/images/terrabase-noble-podman-v2/README.md)
and is not qualified yet.
