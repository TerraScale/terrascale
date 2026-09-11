# TerraBase core backup cohort — 2026-09-11

Frozen source identity before the run:

- HEAD: `9ec8e10ef59b6e6d98a104c39f225fecd3dce95d`
- `Cargo.toml`: `f1148ca9a2e043a5897a6a356bef98c343e3039d9fe6c4178656435dc97d0410`
- `Cargo.lock`: `e78a7896a38f8b344143b10c09adfde4540df9e5e12cd4866a2669b76f4e6010`
- `src/lib.rs`: `312f899f8e7ebf730bc327f9be1853ecfce5f165fe7d5e1a38000c85abde946d`
- `src/backup/manifest.rs`: `6b826d2cb7a365f941f1cc1159f1eb2f0db454a36bbbe16e628cb56e0ea87ed4`
- `src/backup/restore/mod.rs`: `ccbab329867598009aef78c868db9d352c96abddd18d533560687fde8e032eef`
- `src/backup/envelope.rs`: `2f199e496072ed69729a7b3ec0f4ca9b818c18c83dd37225aa1e28add237827a`
- `crates/terrabase-server/src/routed_restore_lifecycle_tests.rs`: `4f21b859736149b0f4eb4f32f8915d14b69bf3e438f42cf6785e87c46ba93e6b`
- `crates/terrabase-server/src/native_backup_runtime.rs`: `db8e5bc84e1a72da4d1821598315a20b334645a5a92c9a1fce0269e746829e94`

Command:

```text
timeout --signal=TERM --kill-after=30s 900s nice -n 19 env CARGO_BUILD_JOBS=2 CARGO_TARGET_DIR=/tmp/terrabase-native-g1-target.lNjLFz cargo test -p terrabase --lib backup:: --locked
```

Result: status 101. `119 passed`, `4 failed`, `1 ignored` in 17.71s.
Log: `/tmp/terrabase-core-backup-tests-final-20260911.log`.

Failures are `ManifestInvalid` in envelope catalog publication, envelope
restore exchange, and scheduled envelope retention; encrypted native restore
failed with `DestinationUnavailable`. These are genuine behavior/fixture
regressions in the current provenance cohort, not test-discovery failures.
