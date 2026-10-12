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

## Scoped TerraBase trust (replaces the old Q-OIDC2; design, not implemented)

The first draft's Q-OIDC2 would have TerraBase on Fly trust every ts-auth token (`auth-issuer`/`auth-audience`). That is
a global trust expansion, and it is **not approved**. The scoped design below lets TerraBase accept **only** the e2e
workload's tokens, for the isolated e2e project, and nothing else. The general ts-auth trust stays off.

### Separation: its own issuer, its own keys, its own audience
| | Platform tokens (unchanged) | e2e workload tokens |
|---|---|---|
| Signing keys | platform ring (`terrascale` instance) | own ring, instance `workload:github-actions-e2e`, provisioned only by the bind command (fail closed like the platform ring) |
| `iss` | `https://auth.terrascale.tech/terrascale` | `https://auth.terrascale.tech/terrascale/workloads/github-actions-e2e` |
| JWKS | `/terrascale/.well-known/jwks.json` | `/terrascale/workloads/github-actions-e2e/jwks.json`, holding only the workload ring's public keys |
| `aud` | `TS_AUTH_ACCESS_TOKEN_AUDIENCES` | exactly `https://api.terrascale.tech/e2e`, never the configured audiences |
| TerraBase | refused on Fly (no `auth-issuer`; unchanged) | accepted by a dedicated verifier, for the e2e project only |

A platform token can never pass the workload verifier: its kid is not in that JWKS, and its `iss` and `aud` differ. A
workload token can never pass the general path, even if that is enabled one day: its `iss` and `aud` differ.

### Pinned identifiers (code constants on both sides, no config)
- TerraBase seeds the e2e organization `00000000000000000000000005` and project `00000000000000000000000006` (enterprise
  plan, no databases), next to the platform seed (`PlatformDatabases`/`PlatformSeed`). E12, E13 and the suite create
  their databases there.
- ts-auth's bind command uses those two ids, not fresh ones. Both sides agree without configuration.

### TerraBase verifier (`Features/UserAuth/WorkloadTokens.cs`, new)
- **Scheme selection:** the `TerraBaseBearer` selector (`ApiKeysExtensions`) sends a JWT whose unvalidated `iss` equals
  the workload issuer to a new `WorkloadBearer` scheme. Everything else goes where it goes today.
- **Keys:** fetched from `http://top1.nearest.of.ts-auth.internal:8090/terrascale/workloads/github-actions-e2e/jwks.json` over
  6PN, so it does not depend on public DNS or the `auth.` cutover. Cached for 10 minutes and refetched on an unknown kid
  at most every 30 s (the `TsAuthKeys` pattern).
- **Accepts only when all of these hold:**
  - ES256, kid in that JWKS, `iss` and `aud` exact
  - `exp` required, and `exp - iat` ≤ 15 minutes, 30 s skew
  - `org_id` = `…05` and `project_id` = `…06`
  - `scope` ⊆ `databases:read databases:write documents:read documents:write`
  - `sub` starts with `github-actions:`
- **Principal:** the existing `ProjectToken` shape for project `…06`, so the existing project checks apply unchanged. A
  route under any other project answers 404, the same as for a token of that other project today.
  Project resolution for this principal uses the seeded catalog entry (`…06` → organization `…05`). It does not use
  `ResolveProjectAsync`, which needs `auth-issuer` and stays off, and it never creates a project namespace.
- **Metrics/logs:** accept and refuse counters (the reason as a bounded label), never the token.
- **Inert until bind:** the workload ring has no keys before the bind, so the JWKS is empty or 404 and nothing verifies.

### ts-auth changes (follow-up commit to #124)
- `GitHubActionsAsync` marks the principal as a workload token. `SignWithKeyRing` then signs it with the
  `workload:github-actions-e2e` ring, sets `iss` to the workload issuer, and sets `aud` to `https://api.terrascale.tech/e2e` only.
- New endpoint `GET /terrascale/workloads/github-actions-e2e/jwks.json` (TerraScale.Endpoints, OpenAPI) publishes that ring.
- `KeyRing`: the workload instance has no implicit first key, like the platform issuer. The bind command provisions it
  (`ProvisionAsync`).
- Bind and unbind use the pinned ids. Unbind deletes the client. A new `revoke-github-actions-e2e-keys` command deletes
  the workload ring, which kills outstanding tokens at TerraBase's next JWKS refresh (10 minutes at most).

### Tests (all with fakes; nothing reaches GitHub or Fly)
- **ts-auth Fast:**
  - Workload tokens carry the workload `iss`, `aud` and kid.
  - The platform JWKS never lists the workload kid, and the workload JWKS never lists a platform kid.
  - An unbound workload has an empty JWKS, and nothing is signed with that ring before the bind.
  - The existing #124 tests stay.
- **TerraBase Fast** (fake workload issuer, like `TestIssuer`). Accepted: e2e documents read/write, database create and
  list on project `…06`, seed present. Refused (401/404):
  - the token on another project's route, or with `project_id`/`org_id` changed (re-signed by the fake)
  - wrong `aud` or `iss`, or a platform-ring kid
  - lifetime over 15 minutes, `exp` missing or expired
  - `alg` `none` or HS256
  - extra scope, or a `sub` that is not a workload
  - an ordinary ts-auth project token with the general trust off (unchanged)
- **E2E:** `Cluster` uses the workload token on Fly. `Gaps.BlockedOnFly` is removed.

### Size and order
About 1,300 lines over 2 PRs: (1) ts-auth workload issuer, keys and JWKS; (2) the TerraBase verifier and seed. Then the
workflow step. Roughly 12–16 hours of agent work including review rounds.

### What the user approves (each separately)
- **Q-OIDC1** (unchanged): run `ts-auth bind-github-actions-e2e` once. This activates the GitHub trust policy and the
  workload ring.
- **Q-OIDC2-scoped:** merge and deploy the TerraBase verifier that accepts **only** workload-issuer tokens, ES256 from that
  ring, `aud=https://api.terrascale.tech/e2e`, for project `…06`. There is no general ts-auth trust and no
  `auth-issuer`/`auth-audience` change.
- **Q-OIDC3:** `permissions: id-token: write` on the `e2e` job, the exchange step, and `Cluster` using the token. Then
  revoke the e2e API key.

## Still needed before the e2e job can use it (not in this change)

1. **The scoped TerraBase trust above (Q-OIDC2-scoped).** The global variant (`auth-issuer`/`auth-audience` for the
   platform issuer) is not approved and is not planned.
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
