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
The TS-768 helper publishes to independent regions concurrently. Each region
retains its own replica ordering, load-balancer drain, readiness/canary gates
and automatic rollback to the previous admitted release.

Required target variables are `TS_AUTO_DEPLOY_CELLS=GRU SFO` and
`TS_AUTO_DEPLOY_ENVIRONMENT=production`. Set them only when the signed specs
authorize `terrabase` and the `ci-release` actor in that environment in both
cells. The scoped service-release OCI user needs the current release-prefix
grant on both head buckets. `TS_ORCH_RELEASE` and `TS_ORCH_SHA256` pin the helper
and must change together. Other variables are `TS_OBJECT_NAMESPACE`,
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
