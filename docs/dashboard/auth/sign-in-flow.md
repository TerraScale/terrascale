# Dashboard sign-in flow

The dashboard signs in through ts-auth's hosted pages as a confidential OIDC
client: authorization code with S256 PKCE, `state` and `nonce`.

## One flow per sign-in

- `GET /login` starts a new flow. The state, nonce, PKCE verifier and
  `returnTo` are encrypted with `TS_DASHBOARD_SESSION_ENCRYPTION_KEY` and stored
  in TerraBase's `dashboard_auth_flows` table.
- The browser gets one HttpOnly, `SameSite=Lax` cookie per flow, named
  `ts-dashboard-oidc-<first 16 hex of SHA-256(state)>`. Its value is a random
  bearer for the stored flow, never the state or the verifier.
- `GET /login/callback` finds the flow from the returned `state`, checks that
  state in constant time, deletes the stored flow and clears that cookie.
  A flow works once.
- Starting a flow never ends another one. Sign-ins started in other tabs, by
  token, MCP or step-up continuations, or by a background request that was
  redirected to `/login` all keep their own flows.

## Lifetime

A flow lives for one hour (`AuthFlows.OidcLifetime`). That must stay longer
than everything a person can do on ts-auth's hosted pages before ts-auth
returns to the callback. The longest step today is an emailed code, which works
for 30 minutes (`Passcodes.Lifetime` in ts-auth). If you lengthen a hosted
step, check this lifetime too.

## Callback outcomes

| Callback | Answer |
| --- | --- |
| Matching, unexpired flow | Code exchange, dashboard session, `303` to `returnTo` |
| Any failure below, while the browser already has a dashboard session | `303` to the flow's `returnTo`, or `/` when it is unknown. This covers a reload, the back button and a second click on ts-auth's Continue link |
| Flow expired | `400` "This sign-in took longer than an hour to finish." with Try again to `/login?returnTo=…` |
| No flow for this state in this browser, or already used | `400` "This sign-in was already used, or was started in another browser." with Try again to `/login` |

Try again starts a new flow. ts-auth still has its own session from the code
the person just entered, so it usually returns straight to the callback
without asking for another code.

Every failure page shows a reference. The dashboard logs it as
`TsAuthSignInFailed` with its cause (`FlowExpired`, `FlowUnknown`,
`AuthorizationDenied`, `SignInRejectedException`, `RateLimited` or the
dependency's exception type), so support can tell a reference's cause from the
logs.
