# Hosted authentication settings

`/auth/branding` edits the selected project's hosted sign-in, sign-up and reset
appearance: name, HTTPS logo, accent, background and text colors, title size,
footer links, and default light/dark/system theme. The local visual preview is an
unsaved draft; Save calls the existing typed ts-dotnet project settings client.
No settings are copied into dashboard storage or browser storage. Failed forms
retain the submitted draft and show the error; Discard restores the saved fields.
Reset to defaults prepares an unsaved project theme, applied only after Save.

`/auth/sign-in` manages the supported email-code, password and self sign-up
methods. At least one first factor must remain available. `/auth/configuration`
sets up to 100 exact redirect URLs and a session lifetime of 300 to 2592000
seconds. An empty policy adds no restriction to the OAuth application's own
exact registered redirects. Lifetime reductions apply at the next session or grant check. The observed expiry
is persisted or the session/grant is revoked; later increases cannot revive it.

`/auth/danger` is the final navigation section. It exposes the existing per-user
delete, per-session revoke and OAuth application delete APIs. Each action requires
the exact project name. It lists the first page of users and sessions; their
respective sections retain paging and individual actions. Reads require any
project role (`viewer`, `member` or `admin`); mutations require the effective
`admin` project role (organization owner/admin or an explicit grant), CSRF validation, and ts-auth's project authorization with the
signed-in user's own token. Branding replacement preserves other settings parts;
the MCP tool preserves appearance fields when changing just name/logo/accent.

After a normal dashboard sign-in, users with multiple organizations choose from
their actual memberships at `/organizations/choose`. Selection uses the existing
`POST /api/v1/dashboard/switch-organization`, which revalidates membership. The
picker supports system/light/dark; authenticated dashboard pages remain dark.
Onboarding and API/CLI/MCP continuations keep their existing routes. OIDC state,
nonce, PKCE, callback URI and logout behavior are unchanged.
