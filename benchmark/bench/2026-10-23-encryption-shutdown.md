# Matched shutdown backlog measurements, 2026-10-23

The encryption branch was slower in all three matched pairs. Median shutdown
was **1,060.487 ms on fixture-patched main versus 5,041.421 ms on the encryption
branch**. Main shipped both segments in all three runs; the encryption branch
exhausted the unchanged four-second shipping budget in two runs. These results
support encryption-associated storage overhead as a contributor to the failure;
they do not establish an isolated cipher-cost estimate or attribute the earlier
individual 4.95-second read observation solely to encryption.

| Pair | Arm | Shutdown ms | Segments | Class passed / failed / skipped | Exit |
|---|---|---:|---:|---|---:|
| 1 | Main | 1104.992 | 2 | 6 / 0 / 0 | 0 |
| 1 | Encryption | 5745.727 | 1 | 5 / 1 / 0 | 2 |
| 2 | Encryption | 5041.421 | 0 | 5 / 1 / 0 | 2 |
| 2 | Main | 793.136 | 2 | 6 / 0 / 0 | 0 |
| 3 | Main | 1060.487 | 2 | 6 / 0 / 0 | 0 |
| 3 | Encryption | 3079.548 | 2 | 6 / 0 / 0 | 0 |

Both arms ran the same six-case `BackupBacklogRecoveryTests` class with normal
parallelism 16, the same 48-document backlog, the same instrumented fixture, and
the same real four-second production deadline. Main base was
`7b80cacbeb683a3501a2c5f0d40a7c618f453c6f` with the fixture-only
`f99573757..12bb923` patch applied: **this is a dirty, fixture-patched baseline,
not an unmodified main result**. The encryption arm was clean
`12bb923203e86853683279336d80f6d04001f076`. Both fixture files had SHA-256
`af5611a4a7ef757ac18f16ca9203b8142dda7b514415afec9dd71306ab3783fe`.

Each arm built once in Release with zero warnings/errors. All six observed
loaded native mappings matched shared release SHA-256
`efd5916aa9ee1eeaa04784813b08e250912ae25d89f8a92499a216c2711f3e8b`.
Execution used delta, nice 19, SDK `11.0.100-rc.1.26425.128`, and clean
ts-dotnet `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`.

The first read's entry-to-trim-hook interval was 356.675–522.602 ms on main
versus 1445.607–1479.992 ms on the encryption branch. The subsequent interval
through read completion was 76.384–237.991 ms versus 266.458–3587.839 ms.
These intervals include multiple operations and scheduling; they are not
isolated cleanup or decryption timings. S3 completion was not instrumented.
Other branch workloads were active on delta, with boundary load averages
30.076–39.019. Alternating paired execution and matching native bytes reduce
confounding but do not isolate the complete branch delta from host contention.

All 36 executions are retained: 34 passed, two failed, zero skipped; aggregate
and wrapper exit 1. The initial harness staging failure is preserved separately;
it ran no tests and did not cause a main rebuild. No failed run was discarded.

Evidence directory:
`~/.t3/test-artifacts/database-encryption/main-encryption-shutdown-3pairs-20261023T191102Z/`.
The report SHA-256 is
`e3285ab8ab4025e8bfee4389b29eeedfc81143ba846407d3df49b5b41a362a2f`;
the 98-entry manifest SHA-256 is
`877127f5e2feb081046b1824373ff7cd8b1aa4a210f830e432829ccd52f6b1b4`.
Original logs, TUnit JSON/HTML, phase traces, source patch and output sets are
retained. This comparison is attribution evidence, not stability qualification.
