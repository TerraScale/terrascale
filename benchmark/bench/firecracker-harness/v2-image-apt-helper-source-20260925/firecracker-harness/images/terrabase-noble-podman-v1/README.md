# `terrabase-noble-podman-v1` image candidate

`recipe.toml` is the schema-2, recipe-version-2.0.0 entry point. It keeps the
candidate identity stable while separating the pinned Bookworm Rust CLI
compiler image from the pinned Ubuntu Noble image that owns `gpgv`, APT,
kernel-build, and ext4 tools. `recipe-v1.0.0.toml` is preserved as historical
input and is not interchangeable with the current recipe.

`packages.lock.json` is reserved for the guest package closure;
`builder-packages.lock.json` is reserved for the Ubuntu builder closure. Each
lock must carry its own six signed-index receipts, successful pinned
`apt-get -s` transcript hashes, dependency edges, and exact `.deb` identities.
The builder lock also records the derived Ubuntu image digest, tool inventory,
and offline image-build argv. The checked-in
`candidate-package-research.json` is only an unverified graph and cannot
authorize fetch or build.

The initial Ubuntu archive keyring and `gpgv` bytes must be identified from
the pinned Ubuntu child image (or from a separately preauthenticated input).
The manifest binds their source paths and SHA-256 values through the Canonical
rootfs and signed-index receipts. The keyring package in the snapshot cannot
authenticate its own download.
The recipe remains unqualified until that trust root, both independent locks,
the builder executable closure, and the derived image digest are pinned.

Final lock hashes are pinned by `recipe.toml`; lock JSON records recipe
identity, version, and package role but does not embed the recipe hash. This
keeps the identity graph acyclic. The derived builder image must not contain
the final recipe or lock bytes. Updating the recipe changes its ext4 UUID and
requires a new recipe hash.

The Rust CLI source-tree digest is SHA-256 over the sorted regular-file
inventory under `crates/fc-image-builder`: each entry contributes its UTF-8
relative path length and bytes, followed by file length and bytes, with both
lengths encoded as unsigned 64-bit big-endian integers. Symlinks and special
files invalidate the input tree.

No kernel, rootfs, OCI image, or benchmark output is checked in here. Image
artifacts and build receipts belong under
`bench/firecracker-harness/image-fixtures/<identity>/` or an explicitly
supplied output directory. No VM, service image, or replication behavior is
qualified by this recipe alone.
