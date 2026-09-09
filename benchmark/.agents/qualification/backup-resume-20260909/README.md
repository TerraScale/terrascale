# Backup qualification checkpoint

Source under test: TerraBase `4f25f8612f23375f9b4c749056c9eece8da8c07e` (tree
`19de9f6fa7a7bb8459f9095e7229e3d7a030733a`).

## Passing evidence

All commands below used `nice -n 19` for Rust processes.

| Command | Result |
| --- | --- |
| `cargo test -p terrabase --lib backup:: -- --nocapture --test-threads=4` | 101 passed, 1 ignored |
| `target/debug/deps/terrabase-* backup::worker::tests::capture_snapshot_is_safe_while_writes_continue --exact --nocapture` | 1 passed |
| `target/debug/deps/terrabase-* backup::worker::tests::native_fleet_restores_both_providers_and_reopens_encrypted_chain --exact --nocapture` | 1 passed |
| `target/debug/deps/terrabase-* backup::worker::tests::sustained_capture_rotates_cache_without_shortening_provider_retention --exact --nocapture` | 1 passed |
| `cargo test -p terrabase --lib backup::object_store::tests` | 12 passed, 1 ignored |
| `cargo test -p terrabase --lib backup::restore` | 10 passed |
| `cargo test -p terrabase-server native_backup_runtime:: --lib` | 4 passed |
| `cargo test -p terrabase-server native_snapshot_backup_tests --lib` | 2 passed |
| `cargo test -p terrabase-server api::tests::restore --lib` | 1 passed |
| `ts-rust: cargo test --all-targets --locked` | all test binaries passed |

The individual and selector logs are retained beside this file. The backup
selector includes encrypted retry, cancellation, object publication, manifest,
envelope, inventory cleanup, and restore publication coverage.

## Gate failures and limits

`make check` is not green. `cargo clippy --workspace --all-targets -- -D
warnings` stops with 118 warnings/errors, including pre-existing dead-code,
interface, and test-lint diagnostics outside the backup implementation. The
failure is retained as a gate failure rather than hidden with broad lint allows.

Production split-shard envelope capture/restore is still unavailable: production
composition rejects installed shard routes, no lifecycle caller reaches
`capture_shard_envelope`, and no multi-participant target reservation/restore
executor exists. Real OCI/Tigris, Vault, workload-identity, provisioning
authority, Firecracker, and multi-region qualification have not been run.

Remote multipart temporary objects can survive a process crash; the S3 API does
not expose ownership, so cleanup must be age- or lease-based before it can be
implemented safely. Restore staging is reclaimed on cancellation/unwind by its
RAII guard, while process-death reconciliation remains open.
