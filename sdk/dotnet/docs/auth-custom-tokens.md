# Custom tokens

`auth.Projects.MintEndUserTokenAsync(projectApiKey, projectId, verifiedUserId, trustedRoles, cancellationToken)`
mints a custom token for a user your backend has already verified. Pass the **whole project API key**, kept on that
backend, and send the returned `AccessToken` to that user through your authenticated application.

The route is `POST /v1/{project}/custom-tokens` with project-key Bearer authentication. The key needs
`auth:tokens:mint`, must belong to the project and must have no database restriction. Roles travel as the trusted
claim `claims.roles`, which TerraBase rules read as `auth.claims.roles`. The token lives 300 seconds and has no
refresh token. Revoking the key stops minting at once; issued tokens validate until they expire.

400/401/403/404/429/503 raise `TerraScaleAuthException`. Every mint is sent once. The token's `ToString()` is
redacted. The route contract is [custom-tokens.json](../contracts/auth/custom-tokens.json); the producer's page is
[custom tokens](https://github.com/TerraScale/terrascale/blob/main/docs/auth/custom-tokens.md).
