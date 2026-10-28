# .NET parity stack after the document foundation merged

The pending runner stack was rebased onto main `445cbf1237825519384d1f1d19212702e83132d0`, which includes the merged core, window and exact-document controls. These are offline qualification results, not latency measurements or live regional evidence.

| Layer | Head checked on delta | `make check` result |
|---|---|---|
| Lanes and counted transports | `6943465b9d59890aea4d45ac1211d90095e8a535` | Passed, 149/149 |
| Cell gates before the clock correction | `0524549fac01d38ff3d0311e9b37499e075e2f04` | Passed, 172/172 |
| Owned run before the clock correction | `1d1c73f2cb9a2b97b32e30f037427b0496fde050` | Passed, 174/174 |
| Full lifecycle before the clock correction | `93b3f1137783ac9baafa0b3e3e36811e468816e5` | **Failed**, 195/196 |
| Corrected cell gates | `f7b0d73eaa87a10a136f449285d16a081e33fc1d` | Passed, 172/172 |
| Corrected owned run | `0eece529df1fcb68b6e3a78bada57e6d47c5d0ef` | Passed, 174/174 |
| Corrected full lifecycle | `7139e5ca36701d8a572b6af9023f1ffd4a44eff5` | Passed, 196/196 |

Each check ran through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19` and the existing `make check` target under `benchmark/clients/dotnet`. All completed the 14 SDK-preparer fixtures, locked restore, Release build with zero warnings/errors, and formatting for all three projects. Successful test runs had zero skips. The SDK pin remains `36d114877a69d18fba33b27ad362bd3e68e9e7ff`.

The failing lifecycle run rejected `ReadinessIsBoundedAndKeptOutOfTheWindowSnapshot(raw-h1)` with `empty` where the test expected acceptance. Its 20 ms wall-clock measurement window could expire before a queued worker started. The correction uses the existing manual clock in cell controls and advances it on fake sends, including the readiness deadline. The same assertions and default parallel execution are retained. This does not change the runner or its real timing rules; the separate window controls still test warmup, full latency, drain, cancellation and reservoir accounting.

Compared with previously reviewed full head `39ee877506bf9e1e034ceff4b2d263956aacdd79`, the final client tree differs only in three test files: `ParityCellTests.cs`, `ParityFakes.cs`, and the visibility of `ParityWindowTests.ManualTime`. All runner source and client documentation are byte-identical. The older stack/review/408 reports retain their original heads and hashes as historical provenance. They do not describe these rebased commit identities.

| Retained log on delta | SHA-256 |
|---|---|
| `/tmp/perf-api-dotnet-lanes-main-check.log` | `03771b08ac0a57db2e5aa814a48ad5b04bd097f8c05a67601bbbf26e3cca4949` |
| `/tmp/perf-api-dotnet-cell-gates-main-check.log` | `2a572d65e9bbeb73513752af9f29f90810d8c8266caf2befaf4ac3a0caeb6712` |
| `/tmp/perf-api-dotnet-owned-run-main-check.log` | `6c49ae5a30ed28f5e039ced0c09ba54df618c8b52a23aa194f83122ae5b21618` |
| `/tmp/perf-api-dotnet-lifecycle-main-check.log` (failed) | `4ebf85af64a6b889ac24889f9d6232c24a1f8991a7d4d4cb21bec48953b91a09` |
| `/tmp/perf-api-dotnet-cell-gates-manual-check.log` | `57344deffee91876e8f9441fdedabb89f71490396668619b35bc9cb690f09a0f` |
| `/tmp/perf-api-dotnet-owned-run-manual-check.log` | `04642cfa19f953cc3b00b8aaf958fc42f744c3d9bbad85e52e3ff6c7d21bc367` |
| `/tmp/perf-api-dotnet-lifecycle-manual-check.log` | `39e763eb00ecc6742b5ccd5b72a438f4233060ebe589f49f325c9751e362bde4` |

The evidence layer changes no client source; its client tree equals the checked lifecycle head above. Required PR CI remains the merge gate. No production requests, credentials or tenants were used. These checks establish neither TLS/ALPN negotiation nor real signal delivery, hidden wire replay counts, remote erasure, or a speedup. The original server-over-engine target remains unmet, and the unresolved run-d owned database is unchanged.

The publication stack was subsequently rebased onto main
`05e7adb5b1b5f0dc043e5eb995cce3b428baa047`. The intervening main change is
auth-only. Every layer's `benchmark/clients/dotnet` tree remains byte-identical
to its checked head above; the rebase adds no new qualification result.

| Layer | Publication head |
|---|---|
| Lanes and counted transports | `2d47625e7b374463a3ffd52f2267f20b8f9ee534` |
| Corrected cell gates | `de959fbf85710884db7c457b32b1d6d8b3b8fe49` |
| Corrected owned run | `20a307d3c2430e6e1acd8025527144d0d260c84b` |
| Corrected full lifecycle | `e266e26483ab89660a2785afc5506f808175f26c` |

All benchmark files in the evidence layer were also unchanged by the rebase;
only this publication note follows it. Required CI on the published PRs remains
pending.
