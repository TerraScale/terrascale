# Early target identity recovery handoff

Product writes stopped after root-directed scope correction. Root must isolate
these changes; no product revert/reset was performed.

Source state: `afe177c7aaa34caed76a5d4afea57a028d558936`.

Before hashes (immutable v47 snapshot):

```
src/engine/mod.rs e9aa338efc28345f8354b1c3f59ce0998513b9d7bbc88532f8d726e72388e720
src/engine/shard_split.rs fc98eec266cdb3c5101c2183d755a94314c0bdab61b519beb7d04110a33d8ed2
src/storage/sqlite/mod.rs d03206cae32993466595849fd729f1754817f568a2a2fee1550e1010fa2afe24
```

After hashes:

```
src/engine/mod.rs 448287b9d718ecb100520f267d38ea2445f6e0e4dd0202a621cf424425783e8c
src/engine/shard_split.rs dde5b38772e03d16015df0006b14b8664f2eb25a2b36bb521cffab241b74c8cd
src/storage/sqlite/mod.rs 8b1a3cd11d2476a0914fd5b209cf1d3b4a50283b9e41330d5620418caa9cd0a1
```

No Cargo/build/test/format commands were run. Requested regression tests were
not implemented. The worktree had broad unrelated dirty changes across
engine, storage, backup, server, TBEP, benchmarks, and evidence paths.
