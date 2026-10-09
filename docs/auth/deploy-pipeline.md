# ts-auth deployment pipeline

Part of [TS-768](https://ku.mariogk.com/terrascale/issue/TS-768).

The pipeline records deployment/qualified before delivery. The orchestrator's
five-minute deploy-lag.yml guard compares this marker with fresh regional
service status source_revision values. It opens an assigned alert when the
oldest pending qualified build exceeds 30 minutes; newer builds cannot reset
that clock. Successful deployment requires fresh target replica proofs.

Every main push runs the required Fast gate and release build in parallel.
`publish-binary` depends on both and publishes the exact single-file binary
and SHA-256 sidecar. The separate Slow workflow does not block delivery.
The main-only deployment job always runs after publication; missing deployment
configuration fails visibly instead of skipping the job.

`scripts/ci/deploy.sh` downloads that release's ARM64 binary, checks its sidecar,
and verifies the deployment helper against `TS_ORCH_SHA256`. The helper is the
amd64 orchestrator binary from the pinned `TS_ORCH_RELEASE`; update these two
variables together. The current helper uses the retired `releases/v1/services`
prefix; the new qualified helper must use the serving `releases/services` prefix
before live acceptance. It reads the service release key from OCI Vault, signs a
cell-scoped claim, and publishes to each cell's regional head bucket. The script
polls every five seconds with a 30-minute regional deadline.
Independent regional signing and delivery require TS-768's qualified helper
release; the current pin still processes regions sequentially. Each cell's native
service controller drains and updates replicas behind its existing readiness
and canary gates, and rolls back automatically if the target cannot serve.

Every green main push deploys without a switch. Until the shared region-discovery
helper is qualified, the script always targets GRU and SFO with their currently
signed `canary` environment. Both signed cell specifications must authorize the
service and the release actor. The scoped OCI IAM policy must permit the service
release prefix in both head buckets. The qualified helper will discover all
serving cells and their signed environments without per-repository lists.
Required variables are `TS_ORCH_RELEASE`, `TS_ORCH_SHA256`,
`TS_OBJECT_NAMESPACE`, `TS_RELEASE_VAULT_ID`, `TS_RELEASE_KEY_SECRET`,
`TS_RELEASE_ACTOR`, `TS_RELEASE_OCI_USER`, `TS_RELEASE_OCI_TENANCY` and
`TS_RELEASE_OCI_FINGERPRINT`. Required secrets are `ORG_READ_TOKEN` and
`TS_RELEASE_OCI_KEY`. Private signing keys stay in Vault and never print.

Require every region's service status to report the expected release complete
and every inventoried replica to serve it. An uploaded artifact or registry
image alone does not prove native deployment. Main jobs queue without cancelling
a release already in progress; the role's prior signed claim remains its
rollback target. Never re-found a serving cell to install a new service build.

Merge a no-op source change for TS-768 acceptance. Record merge time, Fast and
binary-publication completion, regional claim publication and each replica's
first healthy target report. Report merge-to-last-replica and green-to-last-replica
for GRU, SFO and the complete fleet, with the producing CI run link.
