# Runtime-dispatched AEGIS native qualification, 2026-10-23

The corrected library passed the native-only shutdown acceptance on
`mariogk-dev`: 18/18 cases per arm, with no retries, discarded failures or skips.
Encrypted median Stop was 1,416.028 ms, 1.0324x matched main. It also passed
the separate 1.5x ceiling against prior main's 1,060.487 ms median (1.3353x).

| Alternating pair | Main Stop (ms) | Encryption Stop (ms) | Main cases | Encryption cases |
| --- | ---: | ---: | ---: | ---: |
| 1: main, encryption | 1410.207 | 1405.264 | 6/6 | 6/6 |
| 2: encryption, main | 1329.509 | 1416.028 | 6/6 | 6/6 |
| 3: main, encryption | 1371.589 | 1584.242 | 6/6 | 6/6 |
| Median | 1371.589 | 1416.028 | 18/18 | 18/18 |

## Exact inputs

- Main: `7b80cacbeb683a3501a2c5f0d40a7c618f453c6f`, patched only with the exact fixture.
- Encryption: `12bb923203e86853683279336d80f6d04001f076`, unchanged; no later managed correction.
- Both used literal12bb `BackupBacklogRecoveryTests.cs`, SHA-256
  `af5611a4a7ef757ac18f16ca9203b8142dda7b514415afec9dd71306ab3783fe`.
- SDK: `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`; .NET SDK `11.0.100-rc.1.26425.128`.
- Release: [`turso-native-v0.8.2-fts-contract1-runtime-aegis-20261023`](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-runtime-aegis-20261023),
  source `a5ec485f741e2b5d36413f15dd53d6beaa01417a`,
  [maintenance run 40017375521](https://github.com/TerraScale/terrascale/actions/runs/40017375521).

| Architecture | Native SHA-256 |
| --- | --- |
| linux-x64 | `7464a29f3fc9bce603a1e0881720b309a276455a778937d6a9ffc2ed680bd76d` |
| linux-arm64 | `5b20e109146c1c1665e73cea1db407d9ec7b1cc34271474490a402796bd8e697` |

Each invocation ran all six unchanged backlog-recovery methods with normal
parallelism 16 and low CPU priority. The fixture retains 48 documents of 900,000
characters, genuine change-tap reads inside Stop, a 32 MiB read budget, 4 MiB
pages and a four-second shutdown budget. Every invocation exited zero. The Stop
samples come from the original report's `stop.exit duration_ms`, without an
outer timing substitute. Actual `/proc` native mappings matched the x64 candidate
hash in all six invocations.

## Integrated and architecture proof

At the release source above, the exact x64 candidate passed 2,548/2,548 Release
TerraBase Fast tests, 27/27 driver tests and 27/27 executable-only single-file
driver tests. Loaded and extracted native hashes match the candidate. TerraBase
single-file publication includes native bytes without a sidecar. Locked restore,
warnings, assertions and test parallelism were preserved.

The maintenance run contains two independent uncached builds per real
architecture, with byte-identical libraries. Both architectures pass all native
FTS and contract1 smokes. Compiler audits show no global hardware-AES/tuning
requirement and `HAS_HW_AES` undefined. Linked symbols and disassembly retain
runtime dispatch, software fallback, AES-NI and ARMv8 crypto paths.

Original reports and identity receipts remain on `mariogk-dev` under
`~/.t3/test-artifacts/fts-runtime-aegis-acceptance/proof-20261023T202418Z`
and `~/.t3/test-artifacts/fts-runtime-aegis-integrated-20261023T203019Z`.
The published release carries source, build, architecture and smoke receipts.
These timings describe this host and unchanged fixture; they are not production
latency guarantees. Managed ARM64 Fast, execution on CPUs without AES, final
pinned-head CI and production live behavior are not established by this report.
Native cancellation remains cooperative; no strict RSS or hard deadline claim.
