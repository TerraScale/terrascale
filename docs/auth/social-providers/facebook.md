# Facebook sign-in

In **Authentication → Social**, configure Facebook with the client ID and secret from Meta for Developers → Apps → Facebook Login.

Register the exact callback URL displayed by the dashboard: `https://auth.terrascale.tech/ui/{project}/social/facebook/callback`. Requested scopes: `email,public_profile`.

Configure Facebook Login for the app and approve the email permission. Facebook account verification does not attest the email address here; email-code verification is required for an unfamiliar identity.

The equivalent request uses the operator's sign-in token and the admin project role:

```http
PUT /v1/{project}/providers/facebook
Authorization: Bearer {sign_in_token}
Content-Type: application/json

{"client_id": "{provider_client_id}", "client_secret": "{provider_client_secret}"}
```

Secrets are encrypted at rest and never returned. Omit client_secret when updating to retain it. Deleting the provider removes its configuration.

Only a previously connected provider subject selects an existing account. A new provider identity with an unused, provider-verified email can create an account when self sign-up is allowed. Matching email alone never links accounts. Without provider-verified email, a ten-minute pending link requires the emailed code for both new and existing accounts. Verification creates an account when self sign-up is allowed and atomically connects the provider subject; subsequent sign-ins by that subject need no email code. New and existing addresses receive the same response. Connecting from an authenticated Security page also remains available.

Authorization Code uses PKCE S256. Facebook email is always treated as unverified and needs email-code proof before a new subject can create or connect an account.
