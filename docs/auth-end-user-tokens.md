# Trusted backend end-user tokens

`auth.Projects.MintEndUserTokenAsync(projectApiKey, projectId, verifiedUserId, trustedRoles, cancellationToken)`
asserts an identity already verified and authorized by your backend. Pass the **whole project API key**, kept on
that backend. Send the returned `AccessToken` to the authorized end user through your authenticated application;
never relay browser-supplied identity or roles to the mint method without authorization.

The shipped route is `POST /v1/terrascale/projects/{project}/end-user-tokens` with project-key Bearer authentication.
It requires both `documents:read` and `documents:write`, an exact project match and no database restriction.
Organization keys, platform JWTs and cookies are refused. The SDK validates key shape, canonical project/user
ULIDs, and producer role bounds. It cannot determine a key's stored scopes or project binding; the server checks
fresh authority before signing. Only roles can be asserted, with no arbitrary JWT or reserved authority claims.
There is no OAuth substitute grant, account provisioning, refresh token or requested-lifetime setting.

Roles are optional, at most 64, case-sensitive, nonblank and at most 128 UTF-16 code units without controls.
The server removes exact ordinal duplicates. The serialized UTF-8 JSON request must fit 16 KiB, and the complete
signed token must fit 8192 ASCII bytes. A valid per-role set can still exceed the request or final JWT cap.
Success is exactly JSON 200 with Bearer token and `ExpiresIn` of 1–900 seconds, capped at key expiry.
Token `ToString()` is redacted; do not explicitly log or serialize the credential into diagnostics.

400/401/429/503 raise `TerraScaleAuthException` with safe status/code, request ID and Retry-After where provided.
Rate limiting uses a flat OAuth error; other failures use the nested error envelope. Every mint is sent once,
including overload responses. Cancellation does not establish whether a token was issued. Revoking a key stops
future minting; previously issued JWTs validate offline until expiry plus server clock skew (currently 30 seconds).
End-user tokens are rule-bound; they grant no management, SQL or export authority. Missing collection rules deny
access. Set owner/public/trusted-role rules through the authorized project backend, never the end-user token.

Authority: producer `4b6dfd03b2d814cea0b1f7d599478f6b06532b4a`,
[`scoped-end-user-tokens.openapi.yaml`](https://github.com/TerraScale/terrascale/blob/4b6dfd03b2d814cea0b1f7d599478f6b06532b4a/docs/auth/scoped-end-user-tokens.openapi.yaml).
The dedicated [source-derived contract](../contracts/auth/end-user-tokens.json) pins route, schemas, security and
producer hashes. HTTP fixture tests establish SDK behavior; own production rules/refusal qualification remains pending.
