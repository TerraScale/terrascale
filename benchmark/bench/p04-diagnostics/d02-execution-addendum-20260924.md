# D02 execution identity addendum

Status: source and release build evidence only; no D02 guest run is recorded here.
This addendum is outside the sealed source packets and does not change either
protocol's diagnostic semantics.

The 82-file `d02-a2-instrumented-testclosure-20260924` packet (manifest
SHA-256 `051655ddaf012397c610b81160257bb892590925bd9394fe0a68db5eda593a24`,
inventory `ff4252e85a833bd1affcf781886dc776ca1995416565bb34ea7d40e34900e560`)
exists **only for a fresh supporting baseline `test-build`** with a new empty
packet-specific Cargo target directory. Its copied acceptance matrix resolves
the baseline `include_bytes!` path. The prospective guest command in that
packet's `protocol.md`, which passes the 82-file inventory, **must not be used
with the already-built D02 ELF**; the driver syntax-checks that argument but
does not verify it against the binary's build source.

For the existing D02 guest executable, use the frozen
`d02-a2-instrumented-lintfix-20260924/protocol.md` (SHA-256
`468f86c2a8989ea2c054203f997ce546feb107ab2ac2f443ff6f629491840cc1`)
and its prescribed guest arguments. Bind the run to that packet's source
manifest SHA-256 `e6a239c190286259c0aecd855e23b0570b1ce9bda833b26dc9583f4d5476b4a6`,
81-file `--derivative-inventory-sha256`
`deeffbbbc7b1559de4b47c286a904bee64967f20a0d012f66bd8c3da2b9303f7`,
and `--source-delta-sha256`
`88198cfd293d895a49d6830f9b5c83eb781e2e997716984adc95c20fb5b5a5c7`.
The payload is
`d02-a2-gates-lintfix-20260924T224225Z/payloads/p04-d02-musl`, SHA-256
`08876c79991489c3727afb12e07802927d839f0dde341160a2c1ce16143067d4`.
Its `artifact-map.md` and `payloads.sha256` record the build and payload
binding. Verify these hashes before launch and retain the actual guest receipt
under `bench/`. Do not describe this executable as built from the 82-file
supporting-test packet or from the parent a2 measured ELF.
