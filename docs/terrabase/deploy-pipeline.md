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
blocks publication and deployment; TS-773 repairs the underlying main-suite failures.

The main-only deploy job always runs after publication. Missing configuration
fails the job instead of silently making deployment optional.
`scripts/ci/deploy.sh` verifies the ARM64 release binary's SHA-256 sidecar and
the pinned amd64 `ts-orch` helper, then invokes `release service-deploy`.
The qualified helper discovers every region from orch's signed-spec topology
published at Atlas, including regions whose routing health is down or unknown.
It reads each region's fresh admitted environment, then signs, uploads, publishes
and waits independently. A failed region cannot block the others. All regions
use the helper's completion deadline with five-second polling and no timing
override. The retained helper defaults to 30 minutes per region; a TS-783 helper
uses one fixed 20-minute budget across concurrent regions. The existing
90-minute job budget remains a conservative limit for rollout, three downloads
bounded to 15 minutes each, and setup/cleanup. It adds no delay to healthy rollouts.
The retained helper tag and checksum must be qualified together before this
pipeline is activated. Each region retains its replica
ordering, load-balancer drain, readiness/canary gates and automatic rollback
to the previous admitted release.

There is no opt-in, repository-maintained region list or fixed environment.
The initial serving fleet is GRU and SFO, both admitted as `canary` on ARM64.
Signed specs must authorize
`terrabase` and the release actor in each cell. The scoped service-release OCI
user needs the current release-prefix grant on both head buckets.
`TS_ORCH_RELEASE` and `TS_ORCH_SHA256` are pinned in `fast.yml` and must change together;
deployment does not read repository variables for its helper version.
Verified pair: `ci-1e57e6c1736238e1f1d5a910dfe45c0cbdc20151-run-16180`
and `f29dc054716f2ed50ab97960c675753f33c8ac158d6a49f74cd5c190a93ec771`
(successful orch CI 1839, retained amd64 bytes checked against its manifest).
The [pinned source](https://git.terrascale.tech/TerraScale/ts-orchestrator/src/commit/1e57e6c1736238e1f1d5a910dfe45c0cbdc20151/internal/release/cli/service_deploy.go#L45)
declares `--region-timeout` optional, with a `30*time.Minute` default. The
hash-verified pinned binary passes argument admission when it is omitted;
the caller supplies no rollout deadline override.
This helper release is retained as a non-prerelease so CI retention cannot remove it.
The helper uses `releases/services`, the same current prefix as regional
orchestrators. It requires fresh scope-producing controller reports; the
initial activation of that schema belongs to the controller pipeline.
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
