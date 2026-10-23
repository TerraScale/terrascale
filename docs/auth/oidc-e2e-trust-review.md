# E2E OIDC trust review and activation hold

Status on 2026-10-23: **not approved for activation**. Do not run
`bind-github-actions-e2e`, create a grant/client/credential, or widen TerraBase
trust. This is a proposal and review checklist, not an activation record.
The proposed temporary fallback is project-only `client_credentials` through
GitHub Secrets, with its own approval and secure provisioning; no management
credential is needed. This review does not establish that it is provisioned.

## Evidence from current main and public endpoints

- Reviewed TerraScale/terrascale main `49e32e99c`; PR #124 merged inert preparation
  as `20afa8cdee150ab193f3534c54a58e91ad8b4e5e` on 2026-10-12. Current code still
  pins `TerraScale/terrascale` (repository `1414527764`), `ci.yml`, `push`, and
  `refs/heads/main`. Its grant allows database/document reads and writes. Do not
  repurpose that binding for the read-only orchestrator consumer.
- The actual consumer is `TerraScale/ts-orchestrator/.github/workflows/e2e.yml`,
  job `prod`: `workflow_dispatch`, `inputs.target == 'prod'`, main, environment
  `production`. Inspected checkout `1ed9883126b0c4c4635b0ca9df9fdef8fd4bc4ee`.
  It currently has no OIDC exchange or `id-token: write` permission. Changes to
  its consumer and permission need their own reviewed, approved implementation.
- Public GitHub repository metadata reports ts-orchestrator repository id
  `1422428095`, owner `TerraScale`, owner id `206395477`, default branch `main`.
- Safe anonymous probes of `https://auth.terrascale.tech/healthz` and `/readyz`
  returned HTTP 200 (`ok`, `ready`), reporting build
  `888ea413c06f6452cccceab57c735fa35b4d850d`. Discovery returned HTTP 200 and
  advertised the custom grant. This proves neither a binding nor its absence,
  approved permissions, the proposed policy's deployment, or project isolation.
  Regional auth hostnames `gru`, `sjc`, `fra` failed DNS resolution from this host.

The earlier `github-actions-oidc.md` describes a different, legacy consumer and
historical approval claim. It is not authorization for this proposal. No live
binding, secret value, customer data or credential was read during this review.

## Exact proposed boundary — all conditions required together

| Field | Proposed value |
|---|---|
| Grant owner | Proposed: MarioGK, TerraScale operator; acceptance and approval record pending |
| GitHub issuer | `https://token.actions.githubusercontent.com`, exact RS256 signature using its discovered JWKS |
| Exchange audience | `https://auth.terrascale.tech/terrascale`, exact, no trailing-slash folding |
| GitHub organization | `TerraScale`, owner id `206395477` |
| GitHub repository | `TerraScale/ts-orchestrator`, repository id `1422428095` |
| Workflow identity | `TerraScale/ts-orchestrator/.github/workflows/e2e.yml@refs/heads/main` |
| Event and ref | `workflow_dispatch` and `refs/heads/main` |
| Environment and subject | `environment=production`; `sub=repo:TerraScale/ts-orchestrator:environment:production` |
| Reusable workflow | No reusable workflow; absent `job_workflow_ref` only |
| Job and dispatch input | Only job `prod` with `target=prod`; enforce in the reviewed workflow, since GitHub has no standard job/input identity claim |
| Target organization and project | **Pending user selection of exact isolated E2E organization/project ids; no default, lookup by name, or production project substitution** |
| Output scopes | `documents:read` only; no database creation, writes, management, API-key, organization or customer access |
| Output audience | Exact TerraBase API audience to be recorded and approved with its project-scoped verifier; general issuer trust is insufficient |
| Token lifetime | At most 15 minutes; no refresh/identity token; GitHub assertion live with required expiry and at most 30 seconds of skew |
| Replay | Required non-empty scalar-string `jti` and `run_id`; consume `jti` once across replicas |
| Grant expiry | Desired: 24 hours from approved activation, exact UTC deadline recorded; server must refuse new exchanges after that deadline |

A GitHub `project_id`/`org_id` claim or a caller's target parameter is never proof
of project authorization. The reviewed server binding must fix the selected ids;
the receiving TerraBase verifier must require that exact project and audience.
Workflow, branch, environment, event, repository and owner checks cannot replace
that project check. A production-named GitHub environment grants no production
data access. Review the workflow file at the approved source SHA: OIDC does not
attest the job name or dispatch input, and a workflow path alone is not immutable.

## Tests and evidence needed before activation

The legacy grant's Fast integration coverage checks valid bound issuance,
unbound/revoked denial, wrong issuer/audience, repository/owner ids, repository,
workflow, ref, event, missing required claims, expired/not-yet-valid assertions,
wrong algorithms/signatures, replay and scope escalation. Added coverage requires
an exact subject and scalar-string policy claims, and proves caller-supplied
organization/project/admin claims cannot move or widen the issued token.

Existing TerraBase Slow integration coverage (`UserAuthTests`) exercises a real
ts-auth workload token against TerraBase: its own project's database succeeds;
another project and management routes return 404. This is code coverage, not live
readiness evidence. Parent CI must run the changed auth Fast cases and the relevant
TerraBase integration cases; no local suite was run for this preparation.

Before approving the new consumer, extend the same HTTP integration tests for
its exact matrix above: valid selected-project read succeeds; wrong or missing
environment/subject/workflow/ref/event/repo/owner/audience is denied; writes,
other projects, administrative routes, replay, expired tokens and exchanges after
the 24-hour grant deadline are denied. Current legacy code does not implement this
proposal's read-only scopes, consumer pins or grant deadline.

## Approval, audit and revocation

1. MarioGK selects exact isolated organization/project ids and confirms the owner,
   output audience, `documents:read`, and a 24-hour maximum grant duration. Record
   the approval link, policy/workflow source SHAs, UTC start/deadline, verifier
   scope and rollback owner. Until then the ticket stays open and activation held.
2. Implement and review that exact proposal with CI evidence. The current bind
   command generates fresh ids and has no grant expiry; it cannot establish this
   policy. Do not substitute broad persistent trust or run it to discover ids.
3. After explicit approval, the operator performs activation and records only
   non-secret client/project/policy identifiers and timestamps. Verify actual
   stored scope and expiry match the review; a merged PR or discovery response
   does not prove activation.
4. Retain redacted audit evidence: approval, exact policy revision, actor, run id,
   attempt, SHA, target project, effective scopes and UTC deadline. Never retain
   assertions, bearer tokens, authorization headers or signing secrets.
5. Revoke at the deadline or on any failed isolation check: remove the exact
   binding, confirm exchanges fail, and remove its exact verifier permission as
   separately approved. Previously issued offline-validated tokens can remain
   valid for up to 15 minutes. Roll back the consumer to the approved read-only
   GitHub Secrets credential path; preserve data, 120-second shutdown grace and
   deployment rollback protections. No global trust activation is part of rollback.
