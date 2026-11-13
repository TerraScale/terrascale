# Custom OAuth2 sign-in

In **Authentication → Social**, configure Custom OAuth2 with the client ID and secret from Your OAuth2 provider application console.

Register the exact callback URL displayed by the dashboard: `https://auth.terrascale.tech/ui/{project}/social/oauth2/callback`. Requested scopes: `profile email`.

Supply oauth2.authorize_url, token_url, userinfo_url, scopes, id_claim, email_claim and email_verified_claim. URLs must be HTTPS without credentials or fragments. Claim paths are dot-separated and email_verified must be the JSON boolean true. Authorization Code uses PKCE S256.

The equivalent request uses the operator's sign-in token and the admin project role:

```http
PUT /v1/{project}/providers/oauth2
Authorization: Bearer {sign_in_token}
Content-Type: application/json

{"client_id": "{provider_client_id}", "client_secret": "{provider_client_secret}", "oauth2": {"authorize_url":"https://id.example.com/authorize","token_url":"https://id.example.com/token","userinfo_url":"https://id.example.com/userinfo","scopes":"profile email","id_claim":"id","email_claim":"email","email_verified_claim":"email_verified"}}
```

Secrets are encrypted at rest and never returned. Omit client_secret when updating to retain it. Deleting the provider removes its configuration.

Only a previously connected provider subject selects an existing account. A new provider identity with an unused, provider-verified email can create an account when self sign-up is allowed. Matching email alone never links accounts. Without provider-verified email, a ten-minute pending link requires the emailed code for both new and existing accounts. Verification creates an account when self sign-up is allowed and atomically connects the provider subject; subsequent sign-ins by that subject need no email code. New and existing addresses receive the same response. Connecting from an authenticated Security page also remains available.


The configured email_verified mapping is trusted as configured. Choosing a claim that does not prove email ownership is the project's own account-security risk.
