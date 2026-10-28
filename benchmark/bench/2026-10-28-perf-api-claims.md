# Per-request claim materialization, 2026-10-28

Decision: reject this production candidate. Allocation falls, but the three quiet
repeats show a medium c=1 p50 and CPU regression with nonoverlapping ranges.
There is no overall latency win or production proof. The original
server-over-engine <10% target remains unproven. Every request still gets its own
identity and claims; authentication validation, cached tuple contents, expiry,
signing-key refresh and API-key revocation code are unchanged.

The candidate constructs the identity first and gives each fresh claim that
identity as its subject before AddClaim. This avoids the current runtime's clone
of a claim whose subject is null. An index loop also avoids the tuple enumerator
and Select iterator. Mutable claims/principals are never cached or shared.

## Installed-runtime probe

Delta, through `ts-heavy-any nice -n 19`, .NET 11.0.0-rc.1.26425.128;
Claims assembly informational version
`11.0.0-rc.1.26425.128+3551975be08744f0418857c5bed8ab1545c5dd47`.

| Observation | Current form | Candidate form |
| --- | --- | --- |
| Stored claim is the object supplied to the identity | false (cloned) | true |
| Stored claim subject is its owning identity | true | true |
| Type/value/value type/issuer/original issuer and identity defaults match | control | true |
| Mutating one identity/claim leaves a fresh request untouched | true | true |

This is a semantic probe, not a latency/allocation benchmark. Pinned source fetches
for both runtime files returned HTTP 404 at that informational hash; the lookup
was stopped. No downloaded runtime source was used as evidence. The first probe
build failed style rules; the block-scoped corrected source built with zero
warnings/errors. No guard was suppressed.

Artifacts: delta managed `fix-api-token-hash-evidence-72ff07a7a739d3fe9331cba929d7f5d00528b4fe2ed287db776b1e3aba098dd3`,
`benchmark/bench/claims-runtime-probe-{a,b}/`: exact scripts, project/source,
lock, build logs, successful output, hashes and failed source-fetch status.

## Real HTTP validation

On beta through the heavy wrapper, source `4e3b9a40dce19a2c3019c07e2a9e384d99d31ee2`,
clean SDK `36d114877a69d18fba33b27ad362bd3e68e9e7ff`, ran
`make terrabase-test-fast CONFIGURATION=Release TEST_ARGS='--treenode-filter /*/*/UserAuthTests/* --minimum-expected-tests 18'`.

| Check | Result |
| --- | --- |
| Auth Fast cases | 18 passed, 0 failed/skipped |
| New request-identity case | cold request plus 16 concurrent cached requests accepted |
| Claim fields and ownership | default fields preserved, subject is the request identity |
| Mutation isolation | added claim/property on one request absent on every other request |

The fixture uses a separate real server/TLS listener and an IClaimsTransformation
probe. This is targeted coverage; required CI must still run the full Fast gate.
Artifacts: beta managed `fix-api-claims-validation-0c76b3db07a7e9655fd8dfa494d9d8ea5b40a56480739f26820243e029f3bf6a`,
`benchmark/bench/claims-validation-a/` including exact script, source identity,
check logs and empty source/SDK diffs.

## Quiet same-host comparison

Delta through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19`, affinity 0–11,
workstation concurrent GC/tiered PGO, ten-second warmups and five-second keyed
API/read cells. Other heavy slots were reserved. The process order alternated:
before/after, after/before, before/after, after/before. The fourth pair reused the
frozen binaries and replaced the first pair rejected by the unchanged quiet gate.
No production source other than UserAuthenticationHandler.cs differs between arms.
Both server binaries use identical benchmark attribution and the same SDK.

| Identity | Value |
| --- | --- |
| Baseline production source | `f9404808aa8a25d22c7a72a342d9988579e37482` |
| Candidate source and targeted validation | `4e3b9a40dce19a2c3019c07e2a9e384d99d31ee2` |
| SDK, both arms | `36d114877a69d18fba33b27ad362bd3e68e9e7ff` |
| Before server DLL SHA-256 | `a37263db72b4edab5fc63219603244a10667c01056bdf03e3fe0b3a3f6239825` |
| After server DLL SHA-256 | `ca332ab54fd71ec1deadab596ffed26efa6f2952c859ae404e627ffa23b61c06` |

The quiet gate is external CPU mean ≤25%, p95 ≤100% of one CPU, and no external
build/test sample ≥10%. Build servers were disabled in both benchmark builds.
No external build/test samples were observed. Eligibility requires both arms of a
pair to pass, without choosing repeats by their performance results.

| Pair | Arm | External CPU mean % | p95 % | Eligible |
| --- | --- | ---: | ---: | --- |
| A1 | after | 28.68 | 58.53 | no |
| A2 | after | 14.15 | 34.76 | yes |
| A3 | after | 13.8 | 57.49 | yes |
| A1 | before | 42.73 | 91.04 | no |
| A2 | before | 11.44 | 16.88 | yes |
| A3 | before | 11.39 | 20.86 | yes |
| B4 | after | 12.98 | 26.82 | yes |
| B4 | before | 12.9 | 25.82 | yes |

Eligible pairs are A2, A3 and B4. All 32 raw cells, including rejected pair A1,
remain below. Every cell had zero operation errors and nonempty attribution.
These are local harness wall times, not live network or CPU-only server times.

### Eligible repeats: median (min–max)

| Size / concurrency | Before p50 µs | After p50 µs | Before p99 µs | After p99 µs | Before CPU µs/op | After CPU µs/op | Before B/op | After B/op |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| small c1 | 101.7 (98.9–172.4) | 109.4 (105–125.6) | 369.4 (343.6–440.4) | 384.2 (333.9–426.5) | 184.3 (176.2–237.8) | 201.4 (189.8–221.3) | 30570 (30501–30650) | 29913 (29859–29972) |
| small c16 | 177.4 (168.7–178.2) | 171 (169.3–178.9) | 2096.5 (1981.9–2158.8) | 2115.1 (2110.3–2270) | 128.4 (126.9–128.6) | 127.6 (127.5–131.7) | 30084 (30083–30103) | 29399 (29366–29447) |
| medium c1 | 118.6 (118.4–119.2) | 129.8 (121.2–164.8) | 419 (404–419.3) | 450.8 (399.6–486.8) | 206 (203.4–207.1) | 223.9 (207.9–246.4) | 46349 (46323–46386) | 45739 (45642–45782) |
| medium c16 | 214.4 (213.3–219.6) | 209.7 (209.6–216.3) | 2558.9 (2514.1–2564.6) | 2548.3 (2523.7–2611.1) | 161 (160.9–161.2) | 158.8 (156.3–160.1) | 45542 (45528–45546) | 44848 (44818–44888) |

The allocation reduction is consistent. It does not establish a speedup: medium
c=1 p50 rises from 118.6 to 129.8 µs and CPU rises from 206 to 223.9 µs/op,
with nonoverlapping repeat ranges. Small c=1 medians also worsen. c=16 p50 and
p99 ranges overlap. The candidate is archived as an unapplied patch; this branch
changes no production code, tests or harness.

### All raw repeats

| Pair | Arm | Size | c | Ops/s | p50 µs | p99 µs | CPU µs/op | B/op | Server p50 µs | Engine p50 µs | Overhead p50 µs |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A1 | after | medium | 1 | 6365.4 | 125 | 470.5 | 214.8 | 45861 | 85.1 | 4 | 80.9 |
| A1 | after | medium | 16 | 42728.2 | 212.3 | 2554.4 | 158.9 | 44872 | 90.9 | 4.6 | 86.1 |
| A1 | after | small | 1 | 7386.6 | 108.7 | 374.4 | 195.6 | 29959 | 71.7 | 3.1 | 68.2 |
| A1 | after | small | 16 | 56643 | 179.9 | 2145.8 | 130.3 | 29425 | 78.5 | 3.5 | 74.8 |
| A1 | before | medium | 1 | 6209.8 | 126.9 | 457.9 | 220.9 | 46495 | 86.4 | 4 | 81.9 |
| A1 | before | medium | 16 | 36471 | 254.3 | 2838.8 | 172.2 | 45527 | 94.5 | 5.2 | 88.9 |
| A1 | before | small | 1 | 7637.4 | 106.5 | 398.1 | 189.3 | 30601 | 69.3 | 3 | 66 |
| A1 | before | small | 16 | 54566.4 | 185.6 | 2263.4 | 132.9 | 30115 | 79 | 3.6 | 75.1 |
| A2 | after | medium | 1 | 5538.4 | 164.8 | 486.8 | 246.4 | 45782 | 107.9 | 4.9 | 101.4 |
| A2 | after | medium | 16 | 42423.2 | 216.3 | 2548.3 | 158.8 | 44848 | 90.9 | 4.8 | 85.7 |
| A2 | after | small | 1 | 7613 | 105 | 333.9 | 189.8 | 29859 | 68.6 | 3 | 65.3 |
| A2 | after | small | 16 | 56371 | 178.9 | 2270 | 131.7 | 29399 | 78.6 | 3.7 | 74.7 |
| A2 | before | medium | 1 | 6674.4 | 118.4 | 404 | 203.4 | 46386 | 79.6 | 3.6 | 75.8 |
| A2 | before | medium | 16 | 42082.6 | 214.4 | 2514.1 | 161.2 | 45542 | 92.1 | 4.9 | 86.9 |
| A2 | before | small | 1 | 6047.2 | 172.4 | 440.4 | 237.8 | 30650 | 105.1 | 4.3 | 98.8 |
| A2 | before | small | 16 | 57332.6 | 177.4 | 2096.5 | 128.6 | 30083 | 77.9 | 3.5 | 74.1 |
| A3 | after | medium | 1 | 6558.2 | 121.2 | 399.6 | 207.9 | 45642 | 81.8 | 3.7 | 77.8 |
| A3 | after | medium | 16 | 42659 | 209.6 | 2611.1 | 160.1 | 44888 | 90.2 | 4.6 | 85.4 |
| A3 | after | small | 1 | 6524.2 | 125.6 | 426.5 | 221.3 | 29972 | 82.6 | 3.5 | 78 |
| A3 | after | small | 16 | 59328 | 169.3 | 2115.1 | 127.6 | 29447 | 76.7 | 3.5 | 72.9 |
| A3 | before | medium | 1 | 6633.2 | 119.2 | 419.3 | 206 | 46349 | 81 | 3.8 | 76.9 |
| A3 | before | medium | 16 | 41133.2 | 219.6 | 2564.6 | 160.9 | 45546 | 91.1 | 4.9 | 85.9 |
| A3 | before | small | 1 | 8196.8 | 98.9 | 343.6 | 176.2 | 30501 | 64.1 | 2.8 | 61.2 |
| A3 | before | small | 16 | 57020.4 | 178.2 | 2158.8 | 128.4 | 30103 | 77.5 | 3.6 | 73.7 |
| B4 | after | medium | 1 | 6156.6 | 129.8 | 450.8 | 223.9 | 45739 | 87.9 | 4 | 83.4 |
| B4 | after | medium | 16 | 43257.8 | 209.7 | 2523.7 | 156.3 | 44818 | 89.3 | 4.6 | 84.5 |
| B4 | after | small | 1 | 7206 | 109.4 | 384.2 | 201.4 | 29913 | 72 | 3.1 | 68.3 |
| B4 | after | small | 16 | 59215.2 | 171 | 2110.3 | 127.5 | 29366 | 76.2 | 3.5 | 72.5 |
| B4 | before | medium | 1 | 6621.8 | 118.6 | 419 | 207.1 | 46323 | 80.5 | 3.7 | 76.5 |
| B4 | before | medium | 16 | 41712.2 | 213.3 | 2558.9 | 161 | 45528 | 91 | 4.9 | 85.8 |
| B4 | before | small | 1 | 7830 | 101.7 | 369.4 | 184.3 | 30570 | 65.6 | 2.9 | 62.6 |
| B4 | before | small | 16 | 60085.4 | 168.7 | 1981.9 | 126.9 | 30084 | 76.7 | 3.6 | 72.9 |

Attribution measures hosting activity wall time and decorated document-service
wall time, excluding Kestrel parsing/socket work; instrumentation contributes to
overhead. Engine and overhead percentiles are independently calculated. Neither
health subtraction nor a changed denominator is used to claim the <10% target.

Artifacts on delta: managed
`fix-api-claims-materialization-d07404404977969db58040e76b03bf4f77a8a87fbe35397f8651bbde9b29396e`,
`benchmark/bench/claims-qualification-{a,b}/`. They retain exact scripts, CPU
samples/topology, reservation evidence, binary/source hashes, every log and raw
cell, analysis and the 24-cell accepted set. The initial pair's CPU rejection is
retained, not represented as a passing repeat. No live gru/sjc/fra performance
or deployment result is claimed for this rejected candidate.
