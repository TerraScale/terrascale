# GitHub and Google sign-in

The operator creates the provider clients; agents never handle their credentials.
Use the TerraScale organization/project, not a personal production project.

## GitHub

1. Open https://github.com/settings/developers → OAuth Apps → New OAuth App
   (Settings → Developer settings → OAuth Apps).
2. Application name: `TerraScale`. Homepage URL: `https://terrascale.tech`.
3. Authorization callback URL: `https://auth.terrascale.tech/signin/github`.
4. Leave device flow unchecked. Register application.
5. Copy Client ID privately. Generate a new client secret and keep it only for
   the hidden terminal prompt below. This is an OAuth App, not a GitHub App.
6. The signing-in GitHub account must have a verified email. For the requested
   proof, verify `mariogk@terrascale.tech` and select it as the primary email.

## Google

1. Open https://console.cloud.google.com/ and select the TerraScale project.
2. Open Google Auth Platform → Branding → Get started (or APIs & Services →
   OAuth consent screen). Set app name `TerraScale`, support/contact email
   `mariogk@terrascale.tech`, authorized domain `terrascale.tech`.
3. Audience: External; while Testing, add the intended test accounts, including
   `mariogk@terrascale.tech`, under Audience → Test users. Publishing for everyone
   is a separate Google console action; do not claim production availability
   while only test users are admitted.
4. Data Access → Add or remove scopes: `openid`, `.../auth/userinfo.email`,
   `.../auth/userinfo.profile` only. No Gmail or other API permissions.
5. Clients → Create client (or APIs & Services → Credentials → Create credentials
   → OAuth client ID). Application type: Web application; name: `TerraScale`.
6. Authorized JavaScript origins: `https://auth.terrascale.tech`.
7. Authorized redirect URIs: `https://auth.terrascale.tech/signin/google`.
8. Create; keep Client ID and Client secret privately for the hidden prompts.

## One terminal handoff

From this checkout on delta, run:

```sh
python3 scripts/auth/install-social-oauth.py
```

The script prompts without echo for both client IDs and secrets, writes one JSON
credential per provider directly to OCI Vault in gru, and prints only the two
secret OCIDs. It never overwrites an existing secret. Do not paste credentials
into chat, Ku, command arguments or files. A partial failure may have created the
first secret: inspect metadata before retrying instead of rotating anything.

The runtime references are `TS_AUTH_GITHUB_OAUTH_FILE` and
`TS_AUTH_GOOGLE_OAUTH_FILE`, delivered by ts-orch using its existing instance
principal from Vault names `gru-1-ts-auth-github-oauth` and
`gru-1-ts-auth-google-oauth`. No enable switch is needed: a provider without
credentials has no hosted button. Adding these delivery references and rolling
out the reviewed release follows credential creation; no new IAM grants are
assumed or made by this script.

Project clients retain their own credentials and project callback URLs from the
project provider management API. Platform credentials never cross into project
tenants. OAuth uses authorization code + S256 PKCE, browser-bound state, a
single-use durable challenge and verified provider email. Provider tokens are
used only for identity lookup, never stored or returned to the browser.

## Profile integration contract

`GET /v1/terrascale/account/providers` accepts the platform user's live sign-in
access token and returns `{ "providers": ["github", "google"] }` (linked names
only, not provider subjects or secrets). `DELETE` on that path plus `/github` or
`/google` accepts `{ "code": "123456" }`: a fresh email sign-in code, in addition
to the bearer token. Obtain the code through the existing hosted sign-in page.
Unlink returns 204, retains email-code recovery, and revokes every existing
session and token; the user signs in again. A missing or wrong code returns 401.

The user record gains linked provider subjects. The existing unique-claim store
atomically binds each tenant/provider/client/subject to its account; session
lineage checks that claim on subsequent authentication. No TerraBase binary
storage format change is needed. Adding a new provider link advances account
authentication state; repeat sign-in with an existing link does not.
