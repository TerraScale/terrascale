# GitHub Actions OIDC → ts-auth: the e2e suite's credential without a stored secret

The Fly e2e job asks GitHub for an OIDC token and exchanges it at ts-auth for a 15-minute project token. That token
covers the isolated e2e project only, with `databases:read databases:write documents:read documents:write`. Nothing is
stored in GitHub, and the token can create the databases that E12/E13 need.

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

```bash
fly ssh console -a ts-auth -r gru -C "/app/ts-auth bind-github-actions-e2e"
# ts-auth bind-github-actions-e2e: bound: client 01M4GHA0E2E000000000000000, organization <ULID>, project <ULID>
```

- Binding creates the public client with a fresh e2e organization and project id (ULIDs from `AuthStore.NewId`), held in
  the client's properties. They are not platform directory records: no user is a member and no API reaches them, which
  keeps the project isolated. TerraBase enrolls the project on the first database create (below).
- Running it again prints `already bound; nothing changed` with the same ids.
- `ts-auth unbind-github-actions-e2e` deletes the client. Tokens already issued expire within 15 minutes.
- Both commands log `Audit: operator command …`.

## TerraBase trust and bootstrap

TerraBase on Fly trusts ts-auth platform project tokens by default (#231, `fix/fly-platform-auth-defaults`): the issued
token is an ordinary project token, so no TerraBase change or Fly secret is needed for it. Nothing else is trusted more.

- **Bootstrap.** The binding's project exists only as the binding's id; no user, membership or plan in ts-auth reaches it.
  The first `POST /v1/projects/{project}/databases` with the token enrolls the project in TerraBase's catalog
  (`ResolveProjectAsync(create: true)`: a storage namespace bound to the ts-auth issuer, no online check). The online
  project-admin check (`TsAuthProjectAuthorityVerifier`) belongs only to the legacy organization-binding route and is not
  used. TerraBase verifies the token's signature, issuer, audience, expiry and that `project_id` equals the route's project.
- **Isolation.** Every token of this grant names the binding's project only; the caller cannot choose another. Other
  projects answer 404.

## The e2e job (`.github/workflows/ci.yml`, `scripts/e2e/ci-oidc-credential.sh`)

The `e2e` job runs only on a push to `main`, after the deploy, with `permissions: id-token: write`. Each Fly step sources
`scripts/e2e/ci-oidc-credential.sh`, which:

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
5. exports `TERRABASE_E2E_FLY_PROJECT_TOKEN` and `TERRABASE_E2E_FLY_API_KEY` (both the project token, so documents and
   `/sql` use the same project), `TERRABASE_E2E_FLY_PROJECT` and `TERRABASE_E2E_FLY_DATABASE`, and unsets the ID-token
   request variables.

A token lives 15 minutes, so each step builds first (`make terrabase-build`, then `make -o terrabase-build terrabase-e2e`)
and mints its own after the build; a step running longer fails with 401 rather than reusing anything. The script's
self-check (`scripts/e2e/ci-oidc-credential-test.sh`, a fake curl: no request leaves the runner) runs in the `test` job.

With the project token every Fly scenario has `databases:*`, so `tests/TerraBase.E2E/Gaps.cs` no longer skips any scenario
for its credential: a refusal fails the test. Scenarios that stop Fly machines keep their own guard.
Until the binding exists the exchange answers `invalid_client` and the e2e job fails visibly.

The old e2e API key and its three GitHub secrets (`TERRABASE_E2E_FLY_API_KEY`, `_PROJECT`, `_DATABASE`, legacy
TerraBase-only project) are no longer read by CI. They stay installed until a human revokes them
(`scripts/handoff/morning-credentials.sh revoke e2e`).
