# Discord sign-in

In **Authentication → Social**, configure Discord with the client ID and secret from Discord Developer Portal → Applications → OAuth2.

Register the exact callback URL displayed by the dashboard: `https://auth.terrascale.tech/ui/{project}/social/discord/callback`. Requested scopes: `identify email`.

The users/@me response must have verified: true to attest email ownership.

The equivalent request uses the operator's sign-in token and the admin project role:

```http
PUT /v1/{project}/providers/discord
Authorization: Bearer {sign_in_token}
Content-Type: application/json

{"client_id": "{provider_client_id}", "client_secret": "{provider_client_secret}"}
```

Secrets are encrypted at rest and never returned. Omit client_secret when updating to retain it. Deleting the provider removes its configuration.

Only a previously connected provider subject selects an existing account. A new provider identity with an unused, provider-verified email can create an account when self sign-up is allowed. Matching email alone never links accounts. Without provider-verified email, an existing user must verify the emailed code or connect the provider from an authenticated Security page.
