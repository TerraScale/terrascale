# GitHub Actions workload token subject

Amends the issued-token `sub` in [github-actions-oidc.md](github-actions-oidc.md), which stays as written.

## Change

The project token the GitHub Actions grant issues now carries `sub` = `01M4GHA0E2E000000000000000`, the bound
workload's client id (`GitHubActionsGrant.ClientId`). It was `github-actions:1414527764:<run_id>`.

TerraBase accepts only a canonical uppercase ULID as `sub` (`UserAuthenticationHandler`), as for every other ts-auth
token: a client-credentials token's `sub` is its client id, a user's is the user id. The old subject was refused with 401
before any request ran, so the post-deploy e2e job could not create or use `ci-e2e`.

## Behavior

- Audit: the workload is one stable actor in TerraBase. The project it enrolls on its first database creation records
  this client id as its creator. No membership, organization role or binding state is added; the project token's
  `project_id` and scopes still decide access, and the token still reaches only the bound e2e project.
- Run metadata moves out of the token only. ts-auth's audit log line for each exchange still names organization, project,
  `run_id`, `run_attempt`, `sha`, `jti` and scopes. The `jti` stays single-use.
- Lifetime, scopes, claims other than `sub`, and the trust policy are unchanged: 15 minutes, no refresh token.

## Compatibility

No shim. Tokens issued with the old subject were never accepted by TerraBase and expire within 15 minutes. No storage
format, migration or binding change: the existing binding keeps working.

Coverage: `tests/TerraScale.Authentication.Fast/Platform/GitHubActionsGrantTests.cs` (the issued `sub`) and
`tests/TerraBase.Slow/Management/UserAuthTests.cs` (the real ts-auth's workload token creating, writing and reading
`ci-e2e` on a real TerraBase, refused for another project).
