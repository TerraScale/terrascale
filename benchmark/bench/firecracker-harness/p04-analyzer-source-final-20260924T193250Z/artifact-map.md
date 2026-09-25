# P04 analyzer source packet and queued gates

Status: reviewed source frozen with the **pre-refresh** Cargo.lock. No Cargo, build, download, Make gate, host test, guest test, or analysis report ran for this packet while the measured campaign held the heavy slot. The measured P04 binary and its packet are unchanged.

## Immutable source identity

- ts-benchmark revision: `5685f163bb7e9339e8eb0c839fe2512aaf562648` plus the hash-pinned uncommitted analyzer/driver files in `ts-benchmark/` here.
- `packet-source.sha256` covers all 84 copied files and hashes to `0257f4ea4099fd4af9e1d6375338e00b0eef662fdd02ed96b781c3637b9a27fb`. It includes 11 analyzer/driver/protocol files and 73 corrected engine files. `live-analyzer-before.sha256` and `live-analyzer-after.sha256` match; copied driver and complete packet checks passed. Source directories are read-only.
- Reviewed analyzer `src/analysis.rs` SHA-256 `478fc955a4c317a2781751c4f2a69fc0093f1d179d234f9ba3c5ed8cc6331907`; driver `src/main.rs` `879ae9f81f6c34d0408e2295ce3e4f6fe5dd5bf93b502697b2a5626795803366`; adapter `src/terrabase.rs` `c150595f145b2c6cd6bb841f437fe2ce17b148f71046c03a2624101bd66e3aa9`; README `a09fdc112e8514a4408cfa03d1c1bd46320950dbc4f43d8d6cfb234adac25341`; manifest `18cf2c0ca19e2046ffe25b8dfb94889f3ec4bec15e5c15b618cce3fb8b039475`.
- Reviewed Makefile SHA-256 `71e83550e25b76026877ba2187a4b73655be50772805f6aaf3f07742b548faf3`. Its `fmt` and `format` targets cover `main.rs`, `terrabase.rs`, and `analysis.rs`; `make -n` confirmed the low-priority commands and independent review passed. No formatter or build ran during packet preparation.
- P04 protocol `bench/p04-repeat-protocol-20260924.md` SHA-256 `138abc0c939335103311d8edf97a1a060dd68f0d728351f4d5380bd82bde0a22` matches the analyzer's `PROTOCOL_SHA256` constant. The acceptance matrix is copied at its original relative path. All 26 literal `include_bytes!` references resolve to 11 files within this packet, including source, old lock, matrix, and engine files.
- The engine came from the **measured P04 driver packet's corrected Foundation snapshot**, not the changing live Packet A tree. Its 91-entry origin inventory has SHA-256 `6e48da3dc56163ee29ff18c42e24bfc6eae5384114d53609ea198be10a775d4e`; all 73 copied engine entries verified against it. Active path dependencies are `tb-core`, `tb-storage`, and `tb-format`; other copied workspace crates preserve Cargo resolution. `tb-storage/src/mode.rs` is included.

## Lock transition required before any locked build

`old-Cargo.lock` is a byte-identical copy of the packet lock, SHA-256 `3c1a4f2aabd538ad3c6e106dcf1cb6dcec62d302b33d4b4f1666be3868a2a723`. The reviewed manifest pins direct `chacha20 =0.10.2` with `rng`, `rand =0.10.3` with `unbiased`, `flate2 =1.1.2` with `rust_backend`, and `tar =0.4.46` without default features. The old lock already contains the RNG packages but predates these direct edges and lacks the tar/flate2 archive closure; `--locked` gates are expected to reject it.

At the explicitly released heavy slot, derive a **new writable copy** of this packet, run its low-priority `make lock`, save the full old/new lock diff, and accept only dependency edges/packages required by the reviewed manifest. Inspect any other resolved version changes before proceeding; preserve the original source packet and old lock as immutable evidence. Then freeze a new lock-adjusted source inventory and run all gates against that one stable source identity with an external `CARGO_TARGET_DIR`. Do not modify or re-label the measured P04 executable `93e2099f1797033aab0263732bacb551209dad064ccc5847dcd66b3a17cf5ed2` as an analyzer build.

## Queued serial gate scope

Use `ionice -c 3 nice -n 19 make -C <lock-adjusted packet>/ts-benchmark/terra-rust <gate>`; Make itself uses nice/ionice, Cargo `--locked` and two build jobs. Keep each command's log and exit status; recheck the frozen source manifest afterward.

1. `lock` (before freezing the lock-adjusted packet; inspect exact diff)
2. `fmt`
3. `check`
4. `lint` (`--all-targets -D warnings`, including analyzer unit-test source)
5. `check-engine` (real embedded TerraBase API path)
6. `lint-engine` (`--all-targets -D warnings`)
7. `test-build` (feature-enabled musl unit-test ELF, `--no-run`; never execute on host)
8. `build-guest-engine` (feature-enabled static musl release ELF for the analyzer CLI)

After these compile gates, hash and preserve the two static ELFs and verify no `PT_INTERP` or `DT_NEEDED`. The unit-test ELF must run only through the pinned Firecracker controller/guest-agent `test-vm` path. The real `analyze-p04` caller runs inside the analysis VM from its staged `ts-benchmark` root against a sealed campaign manifest and writes fresh JSON/Markdown under `bench/`. Its report must retain its **own** analyzer binary/source/lock identity separately from the measured P04 program and campaign evidence. The actual report and verdict are not part of this source-only packet.

The reviewed image source packet `v2-image-final-source-20260924T190557Z` (manifest SHA-256 `20bddfafde28c73f164a9e6ed5f645bbd3d8a27a0111360a6080fa4c12c1bc96`) is a separate next queue item after analyzer gates.
