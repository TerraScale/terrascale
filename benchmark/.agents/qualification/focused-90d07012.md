# Focused cohort verification

Revision: `90d070127a50d8bc5f89a20f46d1a25bfaa43f43`

Command: sequential nice-19 locked lib tests for server `credential_rotation`, `native_recovery`, `production_lifecycle_control`, `retention`, `export_formats`, `replication`, and core `shard_split`.

Log: [focused-90d07012.log](focused-90d07012.log)

Results: credential rotation 3 passed; native recovery 45 passed; lifecycle control 3 passed; retention 4 passed; export formats 6 passed; shard split 23 passed. Replication selector ran 35 tests and failed 3 (32 passed):

- `replication_peers::tests::dropped_worker_fences_only_its_receipts_and_leaves_survivor_live` — `replication_peers.rs:1656`, stale `peer-old` observation remained.
- `api::tests::global_durability_requires_replication_receipts_before_mutation` — `api.rs:6887`, expected HTTP 201, got 401.
- `replication_peers::admission_tests::real_session_io_failure_fences_old_session_before_restart` — `admission_tests.rs:464`, `NotAdmitted`.

The source tree changed concurrently in `contracts/conformance/{http.json,reactive.json,manifest.json,openapi.json}`, `api.rs`, `native_backup_runtime.rs`, `native_recovery.rs`, and `runtime.rs`. No source files were modified by this lane. Clippy was deferred until these focused failures are resolved.
