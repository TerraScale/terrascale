# GitHub Actions OIDC → ts-auth: the e2e suite's credential without a stored secret

The e2e credential exchange requests GitHub OIDC identity and issues a 15-minute
project token scoped to the isolated e2e project. No reusable token belongs in GitHub secrets.

**Inert until bound.** The code ships with no binding, so every request is refused with `invalid_client`. Binding is the
activation; the user approved it for TerraScale/terrascale's main CI e2e project only (2026-10-14). Agents never run it.

## Grant

`POST /terrascale/oauth/token`, form-encoded:

| Parameter | Value |
|---|---|
| `grant_type` | `urn:terrascale:params:oauth:grant-type:github-actions` |
| `client_id` | `01M4GHA0E2E000000000000000`, a public client that exists only once bound |
| `subject_token` | the GitHub Actions OIDC token, requested with `audience=https://auth.terrascale.tech/terrascale` |
| `scope` | optional; a subset of the four scopes, all four by default |

It is a custom grant rather than RFC 8693 token exchange because OpenIddict validates token-exchange subject tokens as
its own tokens. The parameter names follow RFC 8693.

## Trust policy (`GitHubActionsGrant`, code constants)

- Signed by GitHub: RS256 with a key from `https://token.actions.githubusercontent.com/.well-known/openid-configuration`.
  The IdentityModel configuration manager caches the keys and refetches them when it sees an unknown kid.
- `iss` is GitHub's issuer, `aud` is exactly the platform issuer, the token is live, and clock skew is 30 s.
- Claims must match:
  - `repository_id` 1414527764
  - `repository_owner_id` 206395477
  - `repository` TerraScale/terrascale
  - `ref` refs/heads/main
  - `event_name` push
  - `workflow_ref` `TerraScale/terrascale/.github/workflows/ci.yml@refs/heads/main`
  - `job_workflow_ref`, when present, the same value
- The numeric ids stop a renamed or re-created repository from matching. Pull requests, forks, other branches and
  `workflow_dispatch` never match.
- `job_workflow_ref`: GitHub documents it only "for jobs using a reusable workflow" (the ref path of the called workflow;
  `workflow_ref` and the other standard claims describe the caller). Sources:
  [OIDC reference](https://docs.github.com/en/actions/reference/security/oidc),
  [OIDC with reusable workflows](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-with-reusable-workflows).
  A job of `ci.yml` itself may carry none, so absence is accepted; present with any other value (a reusable workflow
  here or in another repository, another ref, empty, JSON null, a number, an array or an object) is refused. The check
  reads the raw payload, so no typed lookup can hide a non-string value.
- `jti` is single-use on every replica: a unique claim in ts-auth's `uniques` collection. `run_id` is required.
  Shortcut: the claims are never pruned. That is one small document per e2e run; prune them if runs grow.
- Every refusal logs only the name of the claim that failed, never the token. A successful token logs an audit line with
  organization, project, `run_id`, `run_attempt`, `sha`, `jti` and scopes.

The issued token's `sub` is `github-actions:1414527764:<run_id>`. It carries `org_id` and `project_id` (the binding's),
`scope`, and the configured `TS_AUTH_ACCESS_TOKEN_AUDIENCES`. It lives 15 minutes and comes with no refresh token.

## Activation (user-run, after approval), revocation


- Binding creates the public client with a fresh e2e organization and project id (ULIDs from `AuthStore.NewId`), held in
  the client's properties. They are not platform directory records: no user is a member and no API reaches them, which
  keeps the project isolated. TerraBase enrolls the project on the first database create (below).
- Running it again prints `already bound; nothing changed` with the same ids.
- `ts-auth unbind-github-actions-e2e` deletes the client. Tokens already issued expire within 15 minutes.
- Both commands log `Audit: operator command …`.

## TerraBase trust and bootstrap

- **Bootstrap.** The binding's project exists only as the binding's id; no user, membership or plan in ts-auth reaches it.
  The first `POST /v1/projects/{project}/databases` with the token enrolls the project in TerraBase's catalog
  (`ResolveProjectAsync(create: true)`: a storage namespace bound to the ts-auth issuer, no online check). The online
  project-admin check (`TsAuthProjectAuthorityVerifier`) belongs only to the legacy organization-binding route and is not
  used. TerraBase verifies the token's signature, issuer, audience, expiry and that `project_id` equals the route's project.
- **Isolation.** Every token of this grant names the binding's project only; the caller cannot choose another. Other
  projects answer 404.

## The e2e job (`.github/workflows/ci.yml`, `scripts/e2e/ci-oidc-credential.sh`)

The credential exchange:

1. requests a GitHub OIDC token with the URL-encoded `audience=https://auth.terrascale.tech/terrascale`, checks it is one
   compact JWT (three base64url parts, no newline) and masks it before any output;
2. exchanges it for the project token (bearers go through stdin, never the command line), checks its shape the same way
   and masks it; a refusal prints curl's status line only, never a token or response body;
3. reads `project_id` from the token payload, as a route id only (TerraBase enforces it);
4. ensures the database `ci-e2e` with home `gru` and replicas `sjc` and `fra`, sent explicitly: `201`, or `409 name_taken`
   when it exists. It then reads the placement back (`GET …/databases/ci-e2e`). Wrong replicas are set with
   `PATCH …/databases/ci-e2e` at the home region and checked again; a wrong home cannot change, so the step fails naming
   the project, the database and its placement (never a body). It is kept between runs; each test creates and deletes
   its own collection (and any database it creates) in the project;
A token lives 15 minutes, so each step builds first (`make terrabase-build`, then `make -o terrabase-build terrabase-e2e`)
and mints its own after the build; a step running longer fails with 401 rather than reusing anything. The script's
self-check (`scripts/e2e/ci-oidc-credential-test.sh`, a fake curl: no request leaves the runner) runs in the `test` job.
