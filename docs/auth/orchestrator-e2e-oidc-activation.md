# Orchestrator E2E OIDC activation contract

This is the implementation contract for the approved read-only policy in
`oidc-e2e-trust-review.md`. That document retains the historical review state;
the implementation alone does not activate a client, provision targets, or prove
production readiness. The parent/operator owns provisioning, CI and activation.

## Exact exchange

POST `https://auth.terrascale.tech/terrascale/oauth/token` with form fields:

| Field | Value |
|---|---|
| `grant_type` | `urn:terrascale:params:oauth:grant-type:github-actions` |
| `client_id` | `01M4GHA0E2E000000000000000` (public; no client secret) |
| `subject_token` | Fresh GitHub assertion, requested with audience `https://auth.terrascale.tech/terrascale` |
| `scope` | `documents:read` (also the sole default when omitted) |
| Optional `organization`, `project`, `database` | Must exactly match the server binding if present |

Never log assertions, exchange responses, bearer tokens or authorization headers.
The output is an ES256 `at+jwt`, audience exactly `https://api.terrascale.tech`,
subject the client ID, with bound `org_id`, `project_id`, `database` and
`scope=documents:read`. No refresh or identity token is issued. Its absolute
expiry is the earlier of issuance plus 15 minutes and the binding deadline.
The database restriction is the canonical database **name**, matching the current
TerraBase verifier; it is not a database ULID. Organization/project IDs are
canonical uppercase ULIDs. Auth verifies the existing organization/project and
ownership relation at binding and exchange. Auth does not own a database catalog:
the operator must verify the selected database already exists in that project.

GitHub must attest every field below as an exact scalar string, with required
nonempty scalar-string `jti` and `run_id`. A `jti` is consumed once in TerraBase
storage across auth replicas. Retrying an exchange requires a new assertion.

| Claim | Required value |
|---|---|
| `iss`, signature | `https://token.actions.githubusercontent.com`, RS256 from discovered GitHub JWKS |
| `aud` | `https://auth.terrascale.tech/terrascale` |
| `repository`, `repository_id` | `TerraScale/ts-orchestrator`, `1422428095` |
| `repository_owner_id` | `206395477` |
| `workflow_ref` | `TerraScale/ts-orchestrator/.github/workflows/e2e.yml@refs/heads/main` |
| `event_name`, `ref` | `workflow_dispatch`, `refs/heads/main` |
| `environment`, `sub` | `production`, `repo:TerraScale/ts-orchestrator:environment:production` |
| `job_workflow_ref` | Absent; any present value, even matching, is denied |
| Lifetime | Required expiry, live assertion, at most 30 seconds clock skew |

GitHub does not attest job names or dispatch inputs in standard claims. The
reviewed consumer must restrict exchange to job `prod`, `target=prod`, main,
and the `production` environment. Configure that environment for main only;
request `id-token: write` only for the approved job. Path/ref checks do not attest
an immutable workflow revision: retain its reviewed source SHA in activation evidence.

## Operator activation and revocation

1. Record approval, actor, policy/workflow SHAs, exact organization/project ULIDs,
   existing database ULID, UTC activation/deadline, and revocation
   owner. Provision only the approved isolated project/database and expiring,
   database-restricted read-only fallback credential through the parent's approved
   workflow. No synthetic IDs or production target defaults exist in this command.
2. After CI/review/deployment, invoke in the correctly authorized ts-auth workload:
   `ts-auth bind-github-actions-e2e <organization-ULID> <project-ULID> <database-name> <yyyy-MM-ddTHH:mm:ssZ>`.
   All arguments are nonsecret and mandatory. Deadline must be UTC with literal
   `Z`, whole seconds, future, and at most 24 hours from activation. Identical
   live bindings are idempotent. Different, expired, malformed or legacy bindings
   are refused; no silent extension, target replacement or permission broadening.
3. Retain only nonsecret audit evidence and exercise the matrix below. Inspect
   exact stored properties/permissions through an authorized metadata workflow.
   The client stores `ts_organization`, `ts_project`, `ts_database`,
   `ts_activated_at`, `ts_expires_at`; timestamps use the command's UTC format.
   Permissions are token endpoint, this custom grant and `documents:read` only.
4. At deadline or on failed isolation, revoke with the existing exact command:
   `ts-auth unbind-github-actions-e2e`. Confirm new exchanges fail. No data reset,
   global trust change or broader verifier permission is part of revocation.
   Offline tokens issued before revocation survive only until their expiry;
   tokens issued by this policy cannot outlive the recorded binding deadline.

## Required acceptance and denial evidence

| Exercise | Expected |
|---|---|
| Exact assertion, live existing bound target | Token; selected database document read succeeds |
| Wrong/missing/non-scalar subject, environment, workflow, event, ref, repository/owner ID or name | `invalid_grant` |
| Any present reusable workflow claim, including matching string/null/array | `invalid_grant` |
| Wrong/missing/non-scalar issuer/audience, bad algorithm/key/signature, absent expiry, expired or premature assertion beyond skew | `invalid_grant` |
| Missing/empty/non-scalar `jti`/`run_id`, reused assertion, concurrent replica reuse | `invalid_grant`; exactly one race winner |
| Missing/invalid/expired binding property, deleted/mismatched auth target, unbound/revoked client | `invalid_client` |
| Explicit mismatched organization/project/database request | `invalid_target` |
| Writes, database/admin/key/identity/offline scopes requested | Denied (`invalid_request` by OpenIddict or `invalid_scope`) |
| Signed caller-supplied tenant/database/admin/scope claims | Cannot alter output target, scopes or role |
| Token used for another project/database or management API | TerraBase denies; target isolation returns 404 |
| Token used for document/database writes | TerraBase denies; no mutation |
| Deadline approached / reached | Token expiry capped to deadline; exchanges at/after deadline denied |
| Missing bind arguments, invalid ULIDs/name/time, nonexistent or mismatched target, deadline beyond 24 hours, rebind to different target/deadline | Command fails; no replacement binding |

Fast HTTP tests cover exchange/binding/claims/replay/time boundaries against real
auth and TerraBase storage with a fake GitHub discovery/JWKS transport. The real
auth-to-TerraBase `UserAuthTests` case covers selected document reads, cross-target,
write and management denial using a separate fixture-only project credential to
seed data. Parent CI must validate both changed suites; no live activation is
implied by those synthetic integration tests.
