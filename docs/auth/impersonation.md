# Project user impersonation

A project admin can impersonate an enabled end user in that project's tenant. Platform accounts cannot be impersonated. Each tenant has separate user identities; this grants no access to another project's users or to TerraScale management APIs.

In the dashboard, open **Authentication → Users → Impersonate**, enter a reason (1–500 characters), choose the customer application and complete step-up. The ticket is shown once and expires in 60 seconds. Customer apps should provide a support entry point that starts their usual authorization-code flow with a fresh state, nonce and S256 PKCE verifier, adding `impersonation_ticket` to the authorization request. Never put tickets in logs, analytics or browser storage. Prefer a form POST when handing the ticket to the authorization endpoint.

The ticket works once, in its issuing project, with its selected client only. A wrong-client attempt does not consume it. It replaces no regular hosted sign-in: impersonation uses a separate Secure, HttpOnly, SameSite=Lax cookie. Subsequent authorization is restricted to that client until impersonation ends.

## Management API

All operations require a TerraScale person's sign-in token with the project's admin role. Project API keys receive `403 person_required`.

- `POST /v1/{project}/users/{user}/impersonations`, with `{"reason":"Investigate support request","client_id":"<client ULID>"}` and a fresh `start_impersonation` proof in the usual step-up header, returns `{id,ticket,expires_at}`.
- `GET /v1/{project}/impersonations` returns `{items:[...]}`, up to 100 unended sessions within their absolute expiry.
- `POST /v1/{project}/impersonations/{id}/end` ends that impersonation.

The customer's authorization request uses `/{project}/oauth/authorize` with its normal registered redirect, response type, scopes, PKCE, state and nonce plus `impersonation_ticket`. The normal code exchange works without the target's password or second factor. The actor's step-up remains mandatory.

## Check the actor claim

ID tokens, access tokens and UserInfo carry RFC 8693's actor object:

```json
{"sub":"<end-user ULID>","act":{"sub":"<TerraScale admin person ULID>"}}
```

Validate the token's signature, issuer, audience and expiry first. Treat the top-level `sub` as the target and `act.sub` as the support actor. Check `act` on every sensitive application operation, show a visible impersonation indicator in your app, deny account or credential changes, and include both identities in your application's audit log. Do not treat an impersonated token as evidence that the target entered a password or completed MFA.

Hosted auth pages display “You are signed in as X on behalf of Y” with **End impersonation**. Hosted mutations, including password, email, username, MFA, passkeys, linked providers and deletion, return `403 impersonation_read_only` while the impersonation cookie is present. An impersonation cookie cannot authenticate as a regular session.

The absolute session expiry is 60 minutes after redemption. Access, ID, authorization-code and refresh-token expiries are capped at that boundary. Refreshing never moves it. Ending the impersonation, disabling the target, changing either identity's authentication state or removing the actor's admin access invalidates future use at ts-auth. Customer APIs that validate JWTs offline must still enforce their own `act` restrictions; already issued JWTs can remain usable there until their expiry (normally at most five minutes).

Start, redeem and end are audited with the actor, target and reason. `impersonation.started` and `impersonation.ended` enter the webhook outbox atomically with their state changes; their data includes `actor_id`, `user_id`, `reason` and `session_id` (the impersonation id). Expiry denies access immediately; the expiry worker records the end and queues the webhook on its next minute pass.
