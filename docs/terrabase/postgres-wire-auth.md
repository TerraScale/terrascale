# PostgreSQL wire authentication

The postgres listener (`pg.` host, TLS at the platform edge) asks for a cleartext password and accepts two credentials.

| Password | Validation | `database` parameter |
|---|---|---|
| TerraBase API key (`tb_…`) | the API key store, as the HTTP `Bearer tb_…` path | `name` in the key's project, or `project/name` for a key that reaches the whole organization |
| ts-auth project access token (JWT) | exactly the HTTP bearer validation (`UserAuthenticationHandler.ValidateAsync`): asymmetric signature from the published JWKS, `at+jwt`, `auth-issuer`, `auth-audience`, lifetime, `sub`, no guest tokens, ES256 with `kid` for project tokens | `name` in the token's `project_id`, or `project_id/name` |

Customers exchange a ts-auth project key (`<ULID>_<secret>`) for an access token with the `client_credentials` grant
at `https://auth.terrascale.tech/terrascale/oauth/token` and use the token as the password. Tokens last 15 minutes:
reconnect with a new token after expiry.

Access token rules:

- The token must carry `project_id` and grant `documents:read`; writes need `documents:write` (otherwise `42501`).
- A `database` claim restricts the token to that database name.
- The project resolves to its storage namespace through the project catalog, as on the HTTP project routes.
- Every statement re-validates the password and the database, so expiry, key withdrawal and database deletion apply
  to open sessions.

Any credential, project, organization or database the password does not reach answers the same
`28P01 password authentication failed`, so a client cannot tell them apart. A catalog that cannot be read answers
`57P03`.
