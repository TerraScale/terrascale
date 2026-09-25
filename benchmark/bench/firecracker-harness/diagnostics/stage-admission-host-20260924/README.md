# Stage-admission host diagnostics, 2026-09-24

All commands below ran through the host `tools.exec_command` in
`/home/mariogk/Projects/TerraScale/terrabase`, approximately 16:50–17:02 UTC.
They are **non-acceptance diagnostics**. No Firecracker guest ran these tests.
Foundation and the guest operator must repeat the required gates in their
authorized environments. No persistent per-command logs were configured;
the outcomes below come from the tool session output.

Repository HEAD throughout: `352d9f61185d1b685e3032519579040d169ad47d`.
The checkout reports all top-level source files as untracked; the edited file
hashes changed between diagnostic runs and were not captured at each run, so
none of the passing runs proves the later frozen source identity. Before the
edits resumed, `local.rs` SHA-256 was
`1421e79c309d7b446d192d3c03b516d258744bfaac80a8e947618fc608f0e065`
and `run.rs` was
`d8168b7de072f8d35f3b069a50e7d09bb2330d78d2217d10a473f01da6aad642`.

Every Cargo invocation used this host prefix:

```
nice -n 19 env -u RUSTUP_TOOLCHAIN RUSTC=$HOME/.cargo/bin/rustc CARGO_BUILD_JOBS=2 $HOME/.cargo/bin/cargo
```

| Cargo suffix | Host session IDs | Observed diagnostic result |
| --- | --- | --- |
| `check -p tb-storage --all-targets --locked` (twice) | `49154`, `5505` | First failed a `prepare_final` lifetime error; second passed. |
| `test -p tb-storage --test staged_batch_contract --locked` (six iterations) | `95573`, `22288` for the two asynchronously returned sessions; other iterations completed in their command call | In order: compile failed on `unused-must-use`; one test failed on missing read scratch admission; all three failed after a read bank was added but the fixture budget remained 64 MiB; one test failed when the test attempted a new reservation at an already exact disk cap; then three passed; the final four-test run passed. |
| `test -p tb-storage --locked` | `84044` | Unit, floor, prepared, and retained lookup suites passed; scan suite failed in `scans_across_maximum_tiny_run_fan_in_with_bounded_cache` with ENOENT for its guest nofile fixture executable. Later suites did not run in this invocation. |
| `test -p tb-storage --test storage_contract --locked` | `44941` | 13 tests passed. |
| `clippy -p tb-storage --all-targets --locked -- -D warnings` | `40597` | Failed on ten source lints (long methods, casts, option deref, nested `if`, and argument count); source-only corrections followed. |

Two host `rustfmt --edition 2024 --config skip_children=true` runs touched only
the assigned `tb-storage` files. They were formatting actions, not runtime
proof. Cargo created or reused local `target/debug` artifacts. The test
`TempShard` guards removed their `/tmp/terrabase-staged-<pid>-...` shard
directories. A quiescence check at about 17:03 UTC found no owned Cargo,
rustc, or stage-test process and no remaining stage-test shard directory.
`/tmp/terrabase-staged-replacement-build.log` was already present and was
left untouched.
