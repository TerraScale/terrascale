# TerraBase deployment pipeline

Part of [TS-768](https://ku.mariogk.com/terrascale/issue/TS-768).

The pipeline records deployment/qualified before delivery. The orchestrator's
five-minute deploy-lag.yml guard compares this marker with fresh regional
service status source_revision values. It opens an assigned alert when the
oldest pending qualified build exceeds 30 minutes; newer builds cannot reset
that clock. Successful deployment requires fresh target replica proofs.

`fast.yml` runs the required Fast gate and release build in parallel. The release
job stages checked single-file amd64/arm64 executables in a draft. `publish`
depends on both gates and publishes those exact bytes; it never rebuilds them.
Slow runs separately and does not gate production delivery. A failed Fast run
blocks publication and deployment; TS-760 owns the underlying main-suite fixes.

The main-only deploy job always runs after publication. Missing configuration
fails the job instead of silently making deployment optional.
`scripts/ci/deploy.sh` verifies the ARM64 release binary's SHA-256 sidecar and
the pinned amd64 `ts-orch` helper, then invokes `release service-deploy`.
The current pinned helper processes regions sequentially, with a 30-minute
completion wait per region and five-second polling. The job budget is 120
minutes: two regional waits, three downloads bounded to 15 minutes each, and
15 minutes for setup/cleanup. This budget adds no delay to healthy rollouts.
Independent regional signing and delivery require TS-768's qualified helper
release and the matching tag/checksum pin. Each region retains its replica
ordering, load-balancer drain, readiness/canary gates and automatic rollback
to the previous admitted release.

TS-771 owns the standalone removal of repository deployment switches. Its
initial script must target GRU and SFO in their currently signed `canary`
environment. The qualified shared helper will discover every serving region
and its signed environment automatically. Signed specs must authorize
`terrabase` and the release actor in each cell. The scoped service-release OCI
user needs the current release-prefix grant on both head buckets.
`TS_ORCH_RELEASE` and `TS_ORCH_SHA256` pin the helper and must change together.
The current pin uses the retired `releases/v1/services` prefix; live acceptance
requires the qualified helper using `releases/services`.
Other variables are `TS_OBJECT_NAMESPACE`,
`TS_RELEASE_VAULT_ID`, `TS_RELEASE_KEY_SECRET`, `TS_RELEASE_ACTOR`,
`TS_RELEASE_OCI_USER`, `TS_RELEASE_OCI_TENANCY` and `TS_RELEASE_OCI_FINGERPRINT`.
Required secrets are `ORG_READ_TOKEN` and `TS_RELEASE_OCI_KEY`; private signing
keys remain in OCI Vault. Never print their contents.

Main jobs queue without cancelling active deployments. Require the expected
release complete in both regions and a matching healthy report from every
replica. A successful publication alone is insufficient. Preserve serving cells
and data; change them in place. Recovery or a missing regional signing grant
must be diagnosed rather than bypassing Fast or quorum/readiness checks.

For TS-768 live acceptance, merge a no-op source change. Record merge time,
Fast and publication completion, regional claim publication, and every replica's
first healthy target report. Report merge-to-last-replica and green-to-last-replica
for GRU, SFO and the fleet, with CI links. These deployment observations belong
with the ticket; database benchmark results remain in `ts-benchmark`.
