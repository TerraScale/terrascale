# TerraBase backup/restore qualification — final source-bound run

Source:

- HEAD: `4f85998e9a4b3f3c09dda5461f82d82fac5bdee1`
- index tree: `7a8dac7908cf6f7fa247cdfc71277a6ba55f0755`

Checks:

- `nice -n 19 cargo fmt --all -- --check` — PASS (`fmt.log`)
- `nice -n 19 cargo test -p terrabase backup:: --lib` — PASS, 105 passed, 0 failed, 1 ignored (`backup.log`)
- `nice -n 19 cargo check -p terrabase-server --tests` — PASS (`server-check.log`)
- `nice -n 19 cargo test -p terrabase backup::restore::publication_tests --lib` — PASS, 4 passed (`publication.log`)
- `nice -n 19 cargo test -p terrabase backup::worker::tests::retained_remote_history_restores_selected_chain_and_migrates_after_restart --lib` — PASS (`retained-history.log`)
- `nice -n 19 cargo test -p terrabase-server production_byok_provider_names_and_endpoints_fail_closed --lib` — PASS (`byok.log`)

A broader server `restore` selector also passed 21 functional tests but hit the existing `branching_100k_documents_restores_under_one_second` benchmark assertion (14.1s on this host). That benchmark is environment-sensitive and is recorded separately from functional correctness.

Readiness limits that remain:

- `make check`/workspace clippy is still red on broad pre-existing warnings/errors.
- Production split-shard envelope backup/restore is incomplete; installed shard routes are rejected fail-closed and there is no production envelope capture/restore lifecycle.
- OCI/Tigris, Vault, workload-identity broker, provisioning authority, Firecracker, and multi-region qualification have not run here.
- Customer BYOK runtime is AWS-KMS-only while some upstream schemas/docs still advertise unsupported providers.
- Workload credentials may remain in dependency-owned allocations after handoff; multipart orphan cleanup and conditional-copy behavior need live provider qualification.
- Non-Unix restore crash reconciliation fails closed.

Repository gate:

- `make check` — FAIL at workspace clippy (`-D warnings`, 121 reported errors including pre-existing dead-code/interface/test-lint findings). No deployment or push was performed.
