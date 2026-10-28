# Raw request version correction after rebase, 2026-10-28

The correction and offline probe from [the original report](2026-10-28-perf-api-dotnet-raw-request-version.md) were rebased onto main `445cbf1237825519384d1f1d19212702e83132d0`. The original three changed files remain byte-identical to `ed44a70dd2bb59e080f02eceb05f103123f0c8b8`; this report adds the updated-head qualification.

| Check at `ee1cc8bbb188bb72a2c2282aa33296dc0ef7089f` | Result |
|---|---|
| Existing `make check` | Exit 0 |
| SDK preparer fixtures | 14 passed |
| Locked restore and Release build | Passed, 0 warnings/errors |
| Format checks for all three projects | Passed |
| Parallel benchmark tests | 148 passed, 0 failed/skipped |
| Offline request-configuration probe | 4/4 passed |

The job ran on delta through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19`, invoking `bash benchmark/scripts/api-dotnet-raw-version-control-20261028.sh`. The SDK pin remains `36d114877a69d18fba33b27ad362bd3e68e9e7ff`. The probe observes explicit messages in a fake handler; it establishes configuration and does not observe negotiated HTTP, TLS or ALPN. No production calls, credentials, tenant or latency measurement were used.

| Retained delta log | Outcome | SHA-256 |
|---|---|---|
| `/tmp/perf-api-dotnet-raw-version-main-check.log` | Launcher exit 126: invoked the tracked non-executable script directly; no check or probe started | `58bd63c8d2392985d07397e2f05b46ee7aa9e3525b998c583a5894ccdcbceda3` |
| `/tmp/perf-api-dotnet-raw-version-main-check-2.log` | Corrected invocation through `bash`, exit 0 | `faf77e6887cdd77459cd1e756c44985a142f4e57204095cdc1e62b630c330fc0` |

Only this report follows the checked head. Required PR CI is still pending publication. Earlier raw-h2 labels remain insufficient HTTP/2 evidence without independent protocol observation. The separate parity runner is unchanged. No speedup is established, and the original server-over-engine target and denominator remain unchanged and unmet.
