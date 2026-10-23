# Zero-configuration operating contract (TS-795)

This is the required production contract adopted on 2026-10-09. It is not a
claim that the current runtime or deployed fleet already implements it.

- Every feature is always available; no feature or region activation switches.
- Nodes join the private NetBird network, discover serving regions and infer their own region automatically.
- OCI Vault owns production secrets; instance principals and scoped IAM grants govern host reads.
- CI uses GitHub-hosted runners. Production exposes no tuning knobs or authentication bypasses.
- Each value has one correct setting in code. Fakes, demo stores and authentication
  bypasses belong only in test code and test binaries.
- CI rejects new switch-style configuration. A narrow allowance for an unavoidable
  third-party name needs a one-line reason.

See [OCI hosting](../oci.md) for current deployment status and implementation limits.

## Safety boundaries

Automatic admission does not grant customer access or relax tenant isolation,
least privilege, TLS, issuer/audience checks, residency or database placement.
Authorization, customer security enrollment and notification consent still apply.
Destructive operations still require reviewed targets and explicit approval;
health, quorum, ownership, integrity and rollback checks remain mandatory.
Missing credentials or unverified safety evidence must fail closed, without a
production fake or bypass.

## Implementation status

TS-795 corrects policy, documentation and CI feature/deploy opt-ins. It does
not change product code, schemas or runtime configuration assignments.
Orchestrator runtime removal and automation belong to TS-796; TerraBase
runtime toggles belong to TS-773. Other services still need their existing
configuration and secret-delivery paths reconciled with this contract.

Legacy option names, deployed-state observations and test fixture inputs in
technical references describe the current implementation or recorded evidence.
They are not production setup recipes. Existing automation is evidence only
for the operations it actually performs, not for the complete contract.
