# Status administration OIDC client

Startup registers the first-party status client through the same conditional-write
registrar as the dashboard. No client secret or additional configuration is needed.

| Contract | Value |
| --- | --- |
| Issuer | `https://auth.terrascale.tech/terrascale` |
| Client ID | `00000000000000000000000006` |
| Exact callback | `https://status.terrascale.tech/admin/callback` |
| Grant | Authorization code with mandatory S256 PKCE |
| Scopes | `openid email profile` |
| Token authentication | Public client: `client_id`, no client secret |

Use the tenant's discovery document at
`/terrascale/.well-known/openid-configuration`. Authorization and token endpoints
are `/terrascale/oauth/authorize` and `/terrascale/oauth/token`. The status client
has no refresh, client-credentials or token-exchange grant. Sign-in access
tokens expire after one hour; the status service must require sign-in again when
its access token expires. Protect the callback with state and nonce validation,
keep the verifier and tokens on the server, and store no bearer token in browser storage.

Client registration grants no organization access. With the user's sign-in access
token, read `GET /v1/terrascale/organizations/00000000000000000000000001` and check
its `role` for `admin` or `owner`; missing access or a failed lookup must deny
administration. The organization has one role per membership; owner includes admin
authority. `platform_role=admin` is a separate operator-managed user field and is
not an organization membership role. Registration does not grant or restore either.
