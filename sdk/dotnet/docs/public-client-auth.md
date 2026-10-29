# Public-client authentication

The shipped CLI is public client `00000000000000000000000007`, with device,
refresh and resource-exchange grants. It has no authorization-code callback.
Use `Oidc.StartDeviceAuthorizationAsync(clientId, "openid email profile offline_access")`.
Open VerificationUri/VerificationUriComplete, verify the account and explicitly approve.
The SDK starts no browser, sign-in, email request, approval or background poll.

Call `ExchangeDeviceCodeAsync` no sooner than Interval seconds apart, bounded by
ExpiresIn from the original response. Honor authorization_pending and slow_down,
increasing the interval as RFC 8628 requires. Never restart authorization implicitly.
Device/user codes and the complete URI are sensitive; result diagnostics redact them.

Use the public overload `RefreshTokenAsync(clientId, refreshToken)` to rotate.
Persist the result atomically. Never replay after an uncertain outcome; recover
through an independent sign-in instead. `ExchangePublicClientTokenAsync` uses
the same client's live sign-in subject and current organization/project role.
`RevokeTokenAsync(clientId, token)` revokes its own sign-in without a secret.
For a registered public app with a code grant/callback, use
`ExchangePublicAuthorizationCodeAsync` with the original redirect and PKCE verifier.
The producer controls scopes, lifetimes, roles and MFA; no privilege is broadened.

Producer provenance: contracts/auth/public-client-provenance.json. Live qualification
is pending; HTTP fixtures are not live proof. Source/local pack only; NuGet held.
