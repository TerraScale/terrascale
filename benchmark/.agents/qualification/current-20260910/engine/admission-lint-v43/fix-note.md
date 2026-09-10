# Admission lint correction

Baseline source hashes (v43): `src/engine/mod.rs` `dcefbb4b79381957cdd829e5efe0c5b343253ef2fc8ab2b8a648800d67670238`; `src/engine/branch_restore.rs` `ac875916de98e8edef4b875e33e0e38d293622b3de6f1b9bc1900ffebe75e025`.

Bounded changes:

- Kept both admission guards alive while making their fields private lifetime-only `_admission_guard`; `StorageState` ordering remains `port` then guard, and `Engine` guard remains the final field.
- Kept public `Engine::open(EngineConfig)` unchanged; private `open_admitted` now borrows `&EngineConfig`, with owned config retained by `Engine::open` and existing branch-restore callers borrowing their config.
- Removed the redundant `target` argument from `restore_to_staging`; it derives the target from `config.location()`.

Post hashes: `src/engine/mod.rs` `6fa10133fd8c21ddddca70f50ba3bc285176d796d2359556027d521c0aee891d`; `src/engine/branch_restore.rs` `85cac86f13b0335e6d54a5cedd798a646c4fcac409bfea823439dcd0fa66593a`.

Scoped `rustfmt --edition 2024 --check` and `git diff --check` pass. No Cargo/build run; submit selectors to the existing engine gate.

## v44 follow-up

Restored the pre-existing `#[allow(clippy::needless_pass_by_value)]` on the public owned `Engine::open` API and updated the three admission path assertions to `_admission_guard`. A `rg --no-ignore` scan finds no remaining `.admission_guard` reads or old struct-field initializers; the only guard storage is the private lifetime fields in `StorageState` and final `Engine` position. Post hashes: `src/engine/mod.rs` `e9aa338efc28345f8354b1c3f59ce0998513b9d7bbc88532f8d726e72388e720`; `src/engine/branch_restore.rs` `85cac86f13b0335e6d54a5cedd798a646c4fcac409bfea823439dcd0fa66593a`; `src/engine/admission_tests.rs` `571a3830dc8f0d187711ecef097e6b4d374b8f06517dd5e532a6985e7b2501c7`. Scoped rustfmt and diff checks pass.

## v45 follow-up

The lifetime-only underscore field triggered `used_underscore_binding` in the matcher test. The test now acquires a directly named `super::admission::EngineAdmissionGuard` for the relative path, checks relative/canonical/nonmatching paths, drops that guard, and only then opens `Engine`; duplicate exclusion and reopen assertions remain unchanged. No `.admission_guard` field reads remain under `rg --no-ignore`. Post test hash: `src/engine/admission_tests.rs` `b1fba9a6a5dd2362ea2fe77a39f555377efd14aa6fc7723ed78daefe985b1ade`. Scoped rustfmt and `git diff --check` pass; no Cargo/build run.
