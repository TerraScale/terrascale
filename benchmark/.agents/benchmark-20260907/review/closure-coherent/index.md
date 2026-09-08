# Astra packet: coherent fae32 benchmark and compile baseline

Batch: `benchmark-20260907-coherent-fae32`
Source authority: `/tmp/terrabase-qual-fae32` only. The complete authored delta
is copied as [coherent.patch](diffs/coherent.patch), with its original hash
record at `diffs/coherent.sha256`.

## Four substantive changes

1. `bench/scripts/run-terrabase-final.sh` supplies fresh database API/auth and
   ingest health/version/authorization variables, while requiring verified key
   and version.
2. `crates/terrabase-server/Cargo.toml` moves the production `futures-util`
   dependency from dev dependencies to normal dependencies.
3. `crates/terrabase-server/src/delegated_identity.rs` owns tenant batches as
   `Vec<Vec<String>>` before bounded `buffer_unordered(8)`, removing borrowed
   slices from the async future across the exchange await.
4. `src/engine/shard_split.rs` compares `RegionCode` directly, removing an
   invalid dereference.

No warning cleanup is present in `coherent.patch`; no storage lifecycle or
other broad live-worktree changes are included in this cohort.

## Copied source identity

| Input | SHA-256 |
| --- | --- |
| `source/Cargo.toml` | `e5d332ee210d8f2bcb46c04b6a2f0928b728b69049537c2d8dd9cbc1d2a1c82a` |
| `source/server-Cargo.toml` | `5856c99abdc36a9d5893d23a50176142b361ec36e38af9106651dbe284fbd9a0` |
| `source/server-delegated-identity.rs` | `3891b609ca68d92b7dbc5b1b26f5eef44d339aa1de40adbcd3b1e0e9bd77065f` |
| `source/engine-shard-split.rs` | `e36785faf51ad851650b492a4433c62b7fa692477f7d173a759a4ee78e30103f` |
| `source/storage-mod.rs` | `0b706f9e29aa7e6b40ee569f86703145e4254dc2c8af999bbe9d5a6bca9abdf1` |
| `source/storage-sqlite-mod.rs` | `641ee3c419a14505af486d73a0cb37f434a128fecc150db613c74c4b8174b4cc` |
| `source/storage-sqlite-tests.rs` | `bd61e89f0151289ea9e62ebb59079d71385d2089c0c6fbcb54362be56c712495` |

## Callers and verification

The delegated identity production caller is the owned batch refresh around
lines 473-495; its timeout, permit bound, and all-result error collection are
unchanged. The shard caller is `Engine::file_shard_identity` around lines
832-836 in this baseline. The server build log is copied at
`diffs/coherent-server-build.log`; it contains compilation warnings and no
terminal success marker, so session 98599's green result must be supplied by
the build owner against this exact cohort. Benchmark outputs remain pending;
stale release diagnostics are excluded from qualification.

No correctness approval is made by this packet.
