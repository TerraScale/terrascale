# Rebased encryption fixture verification — 2026-10-24

Five focused repetitions passed at source `839065c2360cf8bd870c98502d36019e2a4700c9`: **55/55**, zero failures/skips. The build had zero warnings/errors. Discovery independently matched the same 11 exact case IDs before execution.

| Repeat | Passed | Failed / skipped | Runner exit | Wall seconds |
|---|---:|---:|---:|---:|
| 1 | 11 | 0 / 0 | 0 | 13.983 |
| 2 | 11 | 0 / 0 | 0 | 13.997 |
| 3 | 11 | 0 / 0 | 0 | 14.187 |
| 4 | 11 | 0 / 0 | 0 | 14.243 |
| 5 | 11 | 0 / 0 | 0 | 13.977 |

Scope: the long replicated collection drop, late webhook replay across restart/repair, both replacement-intent arguments, registration clearing a paged intent, genuine current-intent conflict refusal, and all five interrupted-deletion arguments. The [fixture correction](../../docs/terrabase/encryption-rebased-fixtures-2026-10-24.md) explains the four preceding CI failures and preserved safety checks.

Environment: mariogk-dev, 46 CPUs, nice 19; admission load 0.93 / 1.39 / 2.27. Owned full clean source and SDK `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`, installed .NET SDK `11.0.100-rc.1.26425.128`. Repository native download and verification used the runtime-AEGIS release; actual mapped x64 native SHA-256 was `7464a29f3fc9bce603a1e0881720b309a276455a778937d6a9ffc2ed680bd76d` in every repeat. Source revision, assembly hashes, fresh original JSON/HTML, exact IDs, raw `error`/`failed` accounting and native mappings were independently checked. Parallelism 16, minimum expected 1, workload and existing budgets remain unchanged.

There were **two admissions**. The first harness omitted the explicit native-download preparation now required by main: restore passed, build failed for the missing native asset, and zero tests executed. Its five intended repeats were unrun and are preserved as infrastructure failure evidence. The corrected fresh admission ran repository native download/verify, restore, one Release Fast build (81.108 seconds), one discovery and five executions. Neither admission retried a build or test. No source change separated the attempts.

Evidence is retained on delta and mariogk-dev at:

```text
/home/mariogk/.t3/test-artifacts/database-encryption/fixtures-839065c23-nativeprep-dev-20261024T032506Z
```

`report.md`, `independent-audit.json` and `prior-missing-native-attempt/` are the entry points. All 100 manifest files were independently rehashed on both hosts; files/directories were fsynced. Owned processes and branch/admission locks were verified free on delta/dev/beta. Shared source and SDK checkouts were untouched.

- Manifest SHA-256: `68005ca8e5a51e590a680035f7f46d12abbf542545827108d151e6b9a7839385`.
- Archive SHA-256: `b98f8ce5441b234d4ce765fc562d3ed11245d7db9db95b0311bed59a1db1eed3`.

This targeted proof does not replace full exact-head CI or repeat earlier shutdown performance measurements. GitHub CI and the #469-first enqueue hold remain separate gates. Later documentation-only commits do not change the tested fixture source.
