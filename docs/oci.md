# OCI hosting and ts-orch

TerraScale production runs on OCI. The current production host is `ts-gru-1` in
`gru`; moving its workloads under `ts-orch` is in progress. CI runs on
GitHub-hosted runners and publishes container images to GHCR.

| Product region | OCI region |
| --- | --- |
| `gru` | `sa-saopaulo-1` |
| `sjc` | `us-sanjose-1` |
| `fra` | `eu-frankfurt-1` |

These are region identities and the authorized footprint, not evidence that all
three regions currently serve production workloads.

## Control plane and private networking

`ts-orch` owns workload admission and rollout authority. Its documented design
uses regional NATS/JetStream cells and a private NetBird network. Regional
membership, global connectivity and workload readiness are separate facts.
Discovering a host does not establish that its workload is serving.

Automatic regional provisioning and admission are the operating contract;
the orchestrator documentation explicitly records implementation and acceptance
gaps. Do not infer completed migration, cross-region recovery, zero downtime or
production readiness from the design alone. Verify the exact deployed revision
and its readiness before advancing a rollout.

## Object storage

Tigris remains connected to the Fly organization for bucket management. Its
endpoint and storage CLI integration are retained: see [Tigris](tigris.md).

## Secrets

Production secrets belong in the serving region's OCI Vault. Hosts retrieve only
their required secret bundles using OCI instance principals and scoped IAM grants.
Vendor-issued credentials are stored once in Vault; no credential values belong
in repository files, logs or CI artifacts. Automatic first-party secret creation
and delivery are required policy; existing retrieval support alone does not prove
that the full lifecycle is implemented.

## Sources and evidence boundaries

Current host, GHCR and migration status are operator-provided facts (2026-10-22).
The control-plane and secret contracts are documented in
[ts-orchestrator architecture](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/architecture.md),
[automatic regions](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/automatic-regions.md),
[secrets](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/secrets.md) and
[zero configuration](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/zero-configuration.md).
These references describe both intended behavior and current gaps; they are not
new deployment or recovery receipts for TerraBase or ts-auth.
