# Personal access tokens and MCP sign-ins

A personal access token (Profile → Access tokens, 1 to 365 days) and an MCP
sign-in (90 days) each hold their own ts-auth grant, stored encrypted as a
`credential` row of `sessions` ([state storage](../state-storage.md)).

## Expiry

A token works until the expiry chosen when it was created:

- The dashboard computes the expiry once, at ts-auth's callback, in whole
  seconds: the creation time plus the chosen days (90 for an MCP sign-in). That
  one instant is the token's `expires_at`, its credential's expiry and the
  `ts_refresh_expires_at` the dashboard sends when it redeems the grant's
  authorization code. ts-auth accepts it up to 5 minutes past its own now plus
  365 days, so a dashboard clock a little ahead of ts-auth's still creates a
  1-year token. When ts-auth refuses it, the callback answers `422` "This token
  could not be created with that expiry. Choose a shorter expiry." and no token
  is created.
- ts-auth expires that grant's refresh token at the same instant and never
  rotates it
  ([refresh replay](../../auth/regional/refresh-replay.md#grants-that-do-not-rotate)),
  so the dashboard renews the five-minute access token for the token's whole
  life, however often the token is used.
- From that instant the dashboard answers `401` for the token without calling
  ts-auth, and ts-auth refuses the grant's refresh token.

## What ends a token earlier

- Revoking it on Profile → Access tokens: the dashboard marks it revoked,
  revokes the grant at ts-auth (RFC 7009) and deletes its credential.
- Signing out everywhere, a password reset, turning MFA on or off and closing
  the account: ts-auth refuses the grant on the token's next request.
- Removal from an organization or a project limits what the token reaches on
  its next request: the dashboard reads memberships and grants from ts-auth on
  every call.

When ts-auth refuses a token's grant, the dashboard deletes the token's
credential, and the token's row leaves Profile → Access tokens with it.
