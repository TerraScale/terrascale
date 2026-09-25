# Legacy benchmark archive

[`legacy-pre-rust-2026-09-24/`](legacy-pre-rust-2026-09-24/) mirrors the old
repository-relative paths for the .NET TerraBase, Loro, ts-atlas, ts-auth, and
historic `bench/` workloads, reports, fixtures, receipts, and ignored generated
run data. It also contains the former root README, `.gitignore`, and Forgejo
workflow. The archive is evidence/history; active Rust testing starts at the
repository root. Git history before the move remains at revision
`27fb234d5a058059b8a1a415937f4537f15072ff`.

The 2026-09-24 move used same-filesystem renames, with no regular-file content
rewrite or deletion. A recursive pre/post `lstat` comparison, taken before the
symlink relocation described below, matched path, type, device,
inode, mode, size, modification time, and symlink text for **37,229 entries**:
28,747 regular files, 8,347 directories, 131 symlinks, and 4 historic named
pipes. The normalized metadata SHA-256 was
`51b5192285b19c488014c2c2d4bb263ee0f59d84ba66d3c3d40e35c27dc42786`
on both sides. The regular-file logical size was 74,965,822,376 bytes; sparse
files use less physical disk. The move included 7 root paths and 164 `bench/`
children. All 1,448 archived files tracked at the prior Git revision also
matched their original Git blob hashes (16,267,914 bytes read). None of the
relocated symlinks was tracked. Untracked/ignored
generated artifacts remain local and are still
ignored by the root `.gitignore`; preserving this checkout does not make those
artifacts part of a future Git clone.

All 131 symlinks in old run data originally had absolute targets under the
former root `terrabase/` path. Their original literal text and target mapping
were recorded first in the [symlink relocation manifest](legacy-pre-rust-2026-09-24/SYMLINK-RELOCATION-MANIFEST.json).
Only those link objects were then replaced with relative links to the matching
targets under the archived `terrabase/` directory. Every relocated link now
resolves to the intended existing object inside the archive; no active root
compatibility path was created. The manifest SHA-256 is
`27314810c8d244354f3c57238ef7f54a8641addb842978fe612f427853e22cf4`.
Do not use the old
runner, fixture, or receipt as evidence that the Rust acceptance matrix passed.
