# Reviewed P04 analyzer and guest launcher source packet

Status: **source-only**. No Cargo, download, Make build, host test, guest test, launcher run, or analysis report ran while Environment owned the measured campaign slot. The measured P04 driver packet and ELF are unchanged.

## Immutable source identity

- ts-benchmark revision `5685f163bb7e9339e8eb0c839fe2512aaf562648` plus the reviewed uncommitted source in this packet.
- Read-only `packet-source.sha256` covers all **85 copied files**: 12 analyzer/launcher/driver/protocol files and 73 corrected engine files. Manifest SHA-256 is `ff4fe6a0b56482d39341616e75a92ea46c83608f2bac8d65c6327c37a2dcdaf7`. `live-before.sha256` and `live-after.sha256` are byte-identical (SHA-256 `7440698b343273087dba7f1bc0ffc7fac14ee35fd4c3d332216766d40906d813`); copied and post-freeze manifest checks passed.
- Independently reviewed source hashes: `src/p04_analysis_launcher.rs` `266313a33672457f4df534d2ac975d6f0ec4e0007edeac46f6de7d413452ae99`; `src/analysis.rs` `478fc955a4c317a2781751c4f2a69fc0093f1d179d234f9ba3c5ed8cc6331907`; `src/main.rs` `879ae9f81f6c34d0408e2295ce3e4f6fe5dd5bf93b502697b2a5626795803366`; `src/terrabase.rs` `c150595f145b2c6cd6bb841f437fe2ce17b148f71046c03a2624101bd66e3aa9`; `Cargo.toml` `1aa767e2f68f7be8534724d3fb6517460dd76693d7b40cbc8cfb3445d294e028`; `Makefile` `d74c9eed7c449b6b8fec7d6afba3d32a25a8d1772b53e3e57594612d78ee9878`.
- The explicit Cargo binary `p04-analysis-launcher` uses the Git-visible `src/p04_analysis_launcher.rs`; `default-run = "terrabase-sqlite-baseline"` preserves the original CLI. Existing release Make targets explicitly select the driver binary. Dedicated `test-build-launcher` and `build-guest-launcher` targets produce the launcher's musl test and release ELFs. `fmt` and `format` cover all four Rust source files. TOML parsing, `git check-ignore`, affected `make -n` expansions, and `git diff --check` passed before freezing; independent source review found no remaining defect.
- `bench/p04-repeat-protocol-20260924.md` hashes to `138abc0c939335103311d8edf97a1a060dd68f0d728351f4d5380bd82bde0a22`, matching the analyzer's protocol constant. The acceptance matrix and all 26 literal `include_bytes!` references (11 unique inputs) are present at their original relative paths.
- The engine is copied from the **measured P04 driver packet's corrected Foundation snapshot**, not from live Packet A. Its 91-entry origin inventory SHA-256 is `6e48da3dc56163ee29ff18c42e24bfc6eae5384114d53609ea198be10a775d4e`; all 73 copied entries verified. Active path dependencies are `tb-core`, `tb-storage`, and `tb-format`, including `tb-storage/src/mode.rs`; other sealed workspace crates preserve Cargo resolution.

## Lock transition and queued gates

`old-Cargo.lock` is byte-identical to the copied source lock, SHA-256 `3c1a4f2aabd538ad3c6e106dcf1cb6dcec62d302b33d4b4f1666be3868a2a723`. It predates direct RNG/archive dependency edges and has no tar/flate2 package closure. The reviewed manifest pins `chacha20 =0.10.2` with `rng`, `rand =0.10.3` with `unbiased`, `flate2 =1.1.2` with `rust_backend`, and `tar =0.4.46` without default features. Therefore all `--locked` gates must wait for an approved lock refresh.

At explicit heavy-slot handoff, derive a new writable copy of this packet, run low-priority `make lock`, save the full old/new lock diff, and accept only manifest-required dependency edges/packages. Resolve any unrelated version changes before building. Freeze a **new** lock-adjusted inventory, use an external `CARGO_TARGET_DIR`, and run serial low-priority Make gates (`ionice -c 3 nice -n 19 make -C <lock-adjusted packet>/ts-benchmark/terra-rust <gate>`):

1. `fmt`, `check`, `lint` (`--all-targets -D warnings`; both binaries)
2. `check-engine`, `lint-engine` (real embedded TerraBase driver path)
3. `test-build` (feature-enabled driver/analyzer musl unit-test ELF, `--no-run`)
4. `test-build-launcher` (separate launcher musl unit-test ELF, `--no-run`)
5. `build-guest-engine` (feature-enabled analyzer/driver release ELF)
6. `build-guest-launcher` (launcher release ELF)

All Cargo gates use `--locked -j 2` within Make; no host test executable may run. Preserve exact logs, exit statuses, static ELF hashes/sizes and no-`PT_INTERP`/no-`DT_NEEDED` checks, bound to the new post-lock source manifest. The two test ELFs require separate Firecracker `test-vm` executions with pinned controller and guest agent.

## Real guest caller still to verify

The existing harness stages the launcher as the main program and exactly two supplemental payload files: the newly built analyzer ELF at `/root/fc-harness-payloads/p04_analyzer` and a bounded, flat evidence-only compressed campaign bundle at `/root/fc-harness-payloads/p04_a2_bundle`. The **combined** supplemental payload size must fit the harness 128-MiB limit; the launcher also checks a 128-MiB compressed bundle cap, a 512-MiB decoded tar cap, and 256 tar-record cap. Input expansion stays under `/root/fc-bench-data/p04-analyzer-input/<campaign-dir>/`, outside the guest output archive.

The pinned launcher CLI is `p04-analysis-launcher --campaign-dir p04-repeat-20260924-a2 --bundle-sha256 <bundle-hash> --analyzer-sha256 <analyzer-ELF-hash> --campaign-sha256 <campaign-json-hash>`. It verifies the campaign ID against the directory before spawning the analyzer. The child runs with cwd `/root/fc-harness-output`, invoking `analyze-p04` with the sealed campaign JSON and relative `bench/p04-campaigns/<campaign-dir>/reports/{analysis.json,analysis.md}` outputs. The guest output archive should contain those reports and `/root/fc-harness-output/p04-launcher-receipt.json`, with launcher hash, payload hashes, argv, exit code, and status.

Keep three executable identities distinct: the **measured** P04 driver ELF SHA-256 `93e2099f1797033aab0263732bacb551209dad064ccc5847dcd66b3a17cf5ed2` is preserved; the future analyzer ELF (same Cargo driver bin name but newer source/lock) gets a new hash; the launcher ELF is a separate binary. The actual guest caller, archive compatibility, and report validity remain runtime work after build gates. The prior analyzer-only packet `p04-analyzer-source-final-20260924T193250Z` remains preserved; this amended packet supersedes it for later analyzer builds. The separate reviewed image packet `v2-image-final-source-20260924T190557Z` remains next in the heavy queue after analyzer work.
