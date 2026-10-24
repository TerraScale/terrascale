# Encryption CI race qualification, 2026-10-24

Tested source: `86deeb8258038f2cce33a7b384dd12d1d080b9d6`, based on main `e7686d41b`. This report describes that tested commit; later rebases require their own CI.

CI run [40043754763](https://github.com/TerraScale/terrascale/actions/runs/40043754763) failed one concurrent platform-seeding case with a catalog cleanup-intent conflict and four webhook cases with HTTP 429 during fixture polling. Recovery now revalidates a paged intent and its registration in one catalog snapshot, and preserves replacement intents. Tests cover registration clearing the paged intent, replacement before authorization/clearing, genuine interrupted deletion, and refusal of a current conflicting intent. The exact original concurrent-seeding interleaving was not captured.

The webhook fixture repeatedly sent authenticated polling requests while fake time was frozen. The corrected tests await receiver, catalog-commit or intentional-failure log signals, then observe the real API. Production request limits, assertions, deadlines, parallelism 16 and the 120-second test limit are unchanged. The original HTTP 429 response bodies were not retained, so the precise rejection code is unverified.

| Repeat | Passed | Failed | Skipped | Wrapper seconds |
|---|---:|---:|---:|---:|
| 1 | 30 | 0 | 0 | 14.846 |
| 2 | 30 | 0 | 0 | 9.995 |
| 3 | 30 | 0 | 0 | 14.786 |
| 4 | 30 | 0 | 0 | 14.517 |
| 5 | 30 | 0 | 0 | 14.545 |

Each repeat selected the same 30 cases: all 20 Webhooks cases, one concurrent catalog-seeding case, and nine recovery cases including all five interrupted-deletion arguments. Discovery confirmed the exact IDs before execution. Total: 150 passed, zero failures/skips/omissions, no retries. All five test and wrapper exits were zero.

Execution: one mariogk-dev admission, nice 19, .NET SDK `11.0.100-rc.1.26425.128`, clean owned ts-dotnet `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. A full byte-verified copy of the earlier successful Release build was reused; this session performed no builds/restores/native downloads. Actual loaded native SHA-256 in every run: `7464a29f3fc9bce603a1e0881720b309a276455a778937d6a9ffc2ed680bd76d`. Host loads were measured per invocation; no idle-host claim is made.

Earlier harness failures remain visible: one admission stopped before build on the obsolete native-target path; another built successfully but used an unsupported full-path OR filter and executed zero cases. The corrected filter uses the pinned MTP 2.5.1 path-segment alternatives. No failed case result was discarded.

Original reports, commands, actual exits, loads, process maps, source/binary identities, independent audits and hash manifests are preserved on delta and mariogk-dev under `/home/mariogk/.t3/test-artifacts/database-encryption/targeted-86deeb825-retained-dev-20261024T005852Z/`. Prior originals remain unchanged. Evidence was rehashed and fsynced on both hosts; owned processes and admission/branch locks were verified free on all three hosts.

[CI run 40045877626](https://github.com/TerraScale/terrascale/actions/runs/40045877626) passed native, libs, auth, TerraBase, both Slow jobs and the required `test` aggregate on exact tested head `86deeb825`.

Evidence manifest SHA-256: `40018aee113b7ce326a9d5fc7a89073562bfee03af0544efd68d073b7b28277b`.

The subsequent rebase onto main `b2b479d65076f3db32c69387151d9ede5b2a332b` preserves the collection-statistics schema, new wire listener and fixture ports. Main format 15 is retained in the history; encryption initially advanced the branch to 16 for CI. The coordinator subsequently assigned 16 to #469, so encryption now uses 17 for CI. The #469-first queue hold remains. After that PR merges, the encryption branch must rebase again and use the then-current main format plus one; 17 must still be checked against main immediately before queueing. No historical runtime result qualifies the later rebased commit.
