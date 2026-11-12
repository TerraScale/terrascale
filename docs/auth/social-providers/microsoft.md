# Microsoft sign-in

In **Authentication → Social**, configure Microsoft with the client ID and secret from Microsoft Entra admin center.

Register the exact callback URL displayed by the dashboard: `https://auth.terrascale.tech/ui/{project}/social/microsoft/callback`. Requested scopes: `openid email`.

Choose common for work and personal accounts, organizations for work accounts, or consumers for personal accounts. Set tenant in the provider request. Microsoft email is trusted only when the signed xms_edov claim is true. The generic email_verified claim is insufficient because it does not prove domain ownership (the nOAuth risk); all other Microsoft emails require the email-code flow.

The equivalent request uses the operator's sign-in token and the admin project role:

```http
PUT /v1/{project}/providers/microsoft
Authorization: Bearer {sign_in_token}
Content-Type: application/json

{"client_id": "{provider_client_id}", "client_secret": "{provider_client_secret}", "tenant": "common"}
```

Secrets are encrypted at rest and never returned. Omit client_secret when updating to retain it. Deleting the provider removes its configuration.

Only a previously connected provider subject selects an existing account. A new provider identity with an unused, provider-verified email can create an account when self sign-up is allowed. Matching email alone never links accounts. Without provider-verified email, a ten-minute pending link requires the emailed code for both new and existing accounts. Verification creates an account when self sign-up is allowed and atomically connects the provider subject; subsequent sign-ins by that subject need no email code. New and existing addresses receive the same response. Connecting from an authenticated Security page also remains available.
