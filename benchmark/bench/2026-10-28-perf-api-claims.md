# Per-request claim materialization, 2026-10-28

Candidate under measurement; no speedup or production proof yet. The original
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

## Comparison in progress

The same-host three alternating process pairs run on delta, reserved heavy slots,
affinity 0–11, ten-second warmups and five-second keyed API/read cells at c=1/16
and 256/4096 B. Baseline production files are `f9404808aa8a25d22c7a72a342d9988579e37482`;
only UserAuthenticationHandler.cs differs. The harness verifies different server
DLLs and identical SDK DLLs. Build servers are disabled to avoid the orphaned
compiler activity that invalidated earlier comparisons. Eligibility retains the
unchanged external-CPU mean ≤25%, p95 ≤100% of one CPU, and no external build/test
sample ≥10% gate. No latency result will be accepted before all repeats finish.
