# GitHub Actions OIDC → ts-auth: the e2e suite's credential without a stored secret

The Fly e2e job asks GitHub for an OIDC token and exchanges it at ts-auth for a 15-minute project token. That token
covers the isolated e2e project only, with `databases:read databases:write documents:read documents:write`. Nothing is
stored in GitHub, and the token can create the databases that E12/E13 need.

**Inert until bound.** The code ships with no binding, so every request is refused with `invalid_client`. Binding is the
activation and needs the user's explicit approval (Q-OIDC1 in `.cos/e2e-credential-plan.md`). Agents never run it.

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
  - `workflow_ref` and `job_workflow_ref`, both `TerraScale/terrascale/.github/workflows/ci.yml@refs/heads/main`
- The numeric ids stop a renamed or re-created repository from matching. The `job_workflow_ref` check stops a reusable
  workflow called from elsewhere. Pull requests, forks, other branches and `workflow_dispatch` never match.
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

- Binding creates the public client with a fresh e2e organization and project id, held in the client's properties.
- Running it again prints `already bound; nothing changed` with the same ids.
- `ts-auth unbind-github-actions-e2e` deletes the client. Tokens already issued expire within 15 minutes.
- Both commands log `Audit: operator command …`.

## Still needed before the e2e job can use it (not in this change)

1. **Q-OIDC2: TerraBase on Fly must trust ts-auth tokens.** That means TerraBase's `auth-issuer` and `auth-audience`
   for the platform issuer, and the same audience in `TS_AUTH_ACCESS_TOKEN_AUDIENCES`. It is a production security
   change for every tenant. TerraBase creates the e2e project's storage the first time a token for it arrives.
2. **The workflow.** The `e2e` job needs `permissions: id-token: write`, and a step that requests the GitHub token,
   exchanges it and masks it:

   ```bash
   jwt=$(curl -sS -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
     "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=https://auth.terrascale.tech/terrascale" | jq -r .value)
   token=$(curl -sS https://auth.terrascale.tech/terrascale/oauth/token \
     -d grant_type=urn:terrascale:params:oauth:grant-type:github-actions -d client_id=01M4GHA0E2E000000000000000 \
     --data-urlencode "subject_token=$jwt" | jq -r .access_token)
   echo "::add-mask::$token"
   ```

   `Cluster` then uses that token on Fly, which unblocks E12, E13 and the other database-writing scenarios
   (`Gaps.BlockedOnFly`). After that, revoke the e2e API key (`morning-credentials.sh revoke e2e`).
