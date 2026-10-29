---
title: End-user authentication
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# End-user authentication

Every TerraScale project has its own sign-in for the people who use your application: an OpenID Connect issuer, hosted sign-in pages and a user directory that belong to that project alone. Your application signs users in with the standard authorization code flow; you manage the users in the dashboard.

| Item | Value |
| --- | --- |
| Issuer | `https://auth.terrascale.tech/<project ID>` |
| Discovery | `https://auth.terrascale.tech/<project ID>/.well-known/openid-configuration` |
| Hosted sign-in | `https://auth.terrascale.tech/ui/<project ID>/sign-in` |
| Flow | Authorization code with PKCE (`S256`), refresh tokens with `offline_access` |
| Scopes | `openid`, `email`, `profile`, `offline_access` |

End users of one project are separate from every other project and from TerraScale accounts: the same email address in two projects is two unrelated accounts.

Use your project's discovery document for app sign-in. It advertises only `authorization_code` and `refresh_token` grants and the four scopes above. It omits device authorization endpoint metadata. Device authorization, client credentials, token exchange and the GitHub Actions workload grant are platform capabilities, described by the platform discovery document at `https://auth.terrascale.tech/terrascale/.well-known/openid-configuration`.

## Set up sign-in in the dashboard

Open **Authentication** for your project.

1. **OAuth applications**: register your application with a name, its type and its redirect URIs, one per line. Use **public** for browser, mobile and desktop apps (no secret) and **confidential** for server-side apps. A confidential application's client secret is shown once, right after you create it; store it in your server's secret store. Register 1–10 absolute redirect URIs using `https`, or `http` on `localhost` and loopback addresses, without fragments or userinfo (an embedded username or password). Registration rejects invalid URIs with `400 invalid_request`; when the project's redirect allowlist is nonempty, each URI must also exactly match an entry there.
2. **Sign-in methods**: choose email codes, passwords, or both, and whether people can create their own accounts. At least one of email codes and passwords stays on. A method you turn off disappears from the hosted pages.
3. **Branding**: set the name, an `https` logo URL and a `#rrggbb` theme color. They style the hosted pages and the verification, sign-in and password-reset emails; without a name, both show your project's name. Optionally set `email_message` in `branding` through `PUT /v1/{project}/settings`: up to 500 plain-text characters, newlines allowed, added to those emails. A sent `branding` replaces the previous one, so send every branding field each time or omitted fields are cleared. HTML and placeholders such as `{{code}}` appear as literal text.
4. **Configuration**: `redirect_urls` adds a project allowlist of at most 100 exact absolute URLs, without fragments or userinfo (`https`, or loopback `http`). A nonempty list further restricts each client's registered redirects; an empty list never permits unregistered redirects. `session_lifetime_seconds` is an absolute lifetime from original sign-in: 300–2592000 seconds (5 minutes–30 days), default 1209600 (14 days).

Hosted-page **Branding** also supports `title_size` (20–40 px), `default_theme` (`light`, `dark`, `system`), `background_color` and `text_color` (`#rrggbb`), and up to five HTTPS `footer_links` with `label` (1–24 characters) and `url`. Null appearance fields restore defaults; an empty footer list removes project links. Pages read current branding on each request.

Read settings with `GET /v1/{project}/settings`; update them with `PUT` at the same path using your TerraScale sign-in token and project admin role. Each supplied `configuration` or `branding` object replaces that object; omitted objects stay unchanged. Reducing session policy applies at the next session or grant check. Once a shortened browser expiry is observed it is persisted, and an OAuth sign-in observed past policy expiry is revoked; increasing policy cannot revive it. Issued access tokens retain their short expiry.

## Social sign-in

Open **Authentication** for your project, then **Social sign-in**. You can connect four providers: **Google**, **GitHub**, **Apple**, and generic **OpenID Connect**, which works with any OIDC provider by its `https` issuer, such as Auth0, Keycloak or another TerraScale project.

### Create an OAuth client at the provider

Create the OAuth client yourself, with your own account at the provider. Then register the callback URL that the dashboard shows for that provider:

```text
https://auth.terrascale.tech/ui/<project ID>/social/<provider>/callback
```

Here `<provider>` is `google`, `github`, `apple` or `oidc`.

- **Google**: Google Cloud console > APIs & Services > Credentials. Create an OAuth client ID of type **Web application**.
- **GitHub**: Settings > Developer settings > OAuth Apps.
- **Apple**: Apple Developer > Certificates, Identifiers & Profiles. Create a Services ID with Sign in with Apple enabled, and a key with Sign in with Apple enabled.

### Enter the credentials

Paste the client ID and client secret into the provider's form. Apple takes different fields: the Services ID as the client ID, your Team ID, your Key ID, and the `.p8` private key file in place of a secret.

Secrets are stored encrypted and are never shown again. When you edit a provider, leave the secret empty to keep the stored one.

### What end users see

The hosted sign-in and sign-up pages show a **Continue with Google**, **Continue with GitHub**, **Continue with Apple** or **Continue with single sign-on** button for each provider you configure. If your application already uses the hosted pages, it needs no code change: the same authorization code flow described above handles social sign-in.

An end user signs in only with a provider that confirms a verified email address:

- A user is matched to an existing account by that email, so email codes, passwords and social sign-in reach the same account.
- A new email creates an account only if the project allows sign-up.
- Disabled users and users with MFA cannot sign in this way.

### Manage providers through the API

These calls need the project admin role and your TerraScale sign-in token.

| Method and path | Purpose |
| --- | --- |
| `PUT /v1/{project}/providers/{provider}` | Configure a provider. Body: `client_id`, `client_secret`, `issuer` (`oidc` only), `team_id` and `key_id` (`apple` only). |
| `DELETE /v1/{project}/providers/{provider}` | Remove a provider. |
| `GET /v1/{project}/settings` | List the providers with `configured` and `callback_url`. |

## Sign users in

Use a maintained OpenID Connect library with authorization-code and PKCE support, configured with your exact project issuer and discovery URL. Use its authorization and callback validation APIs:

1. Create a short-lived, browser-bound, single-use authorization attempt containing cryptographically random `state`, `nonce` and PKCE verifier. Keep the verifier private and bind the attempt to the initiating browser session, expected issuer, client ID and redirect URI.
2. Send the browser to the discovered authorization endpoint with `response_type=code`, `client_id`, an exactly registered `redirect_uri`, the scopes, `state`, `nonce`, and the verifier's `S256` `code_challenge` with `code_challenge_method=S256`. Request `offline_access` if you need refresh tokens.
3. At the callback, require the initiating browser's attempt and an exact `state` match before accepting either success or error. Reject missing, expired, mismatched or reused attempts and duplicate response parameters. Consume the attempt atomically once; then handle an OAuth error or redeem `code` with that attempt's original `redirect_uri` and `code_verifier`. Public clients send `client_id`; confidential clients authenticate with their server-held secret. The token endpoint validates PKCE; never retry a consumed code automatically.
4. Before creating your app session, have the library validate the ID token's signature using the trusted issuer's discovered JWKS and permitted algorithm, exact `iss`, `aud` for your client (and `azp` where required), time claims (`exp`, `iat`, `nbf` if present) and exact attempt `nonce`. Decoding a JWT is not validation. If you call the discovered userinfo endpoint with the access token, require its `sub` to equal the validated ID token's `sub` before using its claims.

The access token is a JWT with your project's issuer as `iss`, your client ID as `aud` and the user's ID as `sub`. Your API must verify its signature, issuer, audience and time validity; an ID token is for the client sign-in, not API authorization. `/oauth/userinfo` returns `sub`, and `email` with the `email` scope. Access tokens last 5 minutes.

Refresh tokens rotate on every use. The initial project refresh token expires at **original sign-in (`auth_time`) + configured project session lifetime** (5 minutes–30 days, default 14 days). Rotation never extends the observed expiry; a policy reduction can shorten it further. Store each successor securely and stop using its predecessor. Keep tokens and client secrets out of URLs, logs and browser persistent storage. On sign-out, clear your app session, use `/oauth/logout` for hosted sign-out and `/oauth/revoke` for token revocation; these are separate from clearing local state.

A project issuer serves only the authorization code and refresh token grants. Project clients also support RFC 7009 token revocation at the discovered `revocation_endpoint`. Project tokens are for your own application: TerraScale's APIs do not accept them.

## Manage users

The dashboard lists your project's users with search. For each user you can see their sessions, end a session, **disable** them or **delete** them. Disabling a user ends every session and stops their refresh tokens at once; access tokens already issued stay valid until they expire, at most 5 minutes. Deleting a user removes the account; the address can sign up again.

For automation, the [.NET SDK project administration client](https://github.com/TerraScale/ts-dotnet/blob/main/src/TerraScale.DotNet/AuthService/TerraScaleAuthProjects.cs), `TerraScaleAuthClient.Projects`, exposes settings read/update, provider set/delete, user list/get/disable/enable/delete, session list/revoke and OAuth client list/create/delete. These `/v1/{project}` calls use your **TerraScale account sign-in token**, with viewer access for reads and admin for changes, not a project end-user token. This is separate from interactive OIDC login; other SDKs do not necessarily expose this management group. See [C# source installation](/zh-cn/getting-started/csharp) for the SDK.

### Export the directory

Project admins export the directory with `GET /v1/{project}/users/export`. Each request needs your TerraScale sign-in token and the admin role. Each page is recorded in the organization audit log as `project.users_exported` before its data is returned.

- `limit`: 1 to 100, default 100.
- `format`: `json` (default) or `csv`.
- `after`: the previous page's cursor, from `next` in the JSON body or the `X-Export-Next` header. Omit it on the first page.

A JSON page has `items`, `next` and `created_before`. Both formats send the cutoff in `X-Export-Created-Before`. Repeat the request with `after` until the response has no `X-Export-Next` header. Save each page before you request the next one, and deduplicate by `id`, because a retried page may show updated fields.

```bash
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
api -G "https://auth.terrascale.tech/v1/$PROJECT/users/export" --data-urlencode limit=100 > page-1.json
```

This is a creation-cutoff scan, **not a transaction snapshot**. Accounts created at or after the first page's cutoff are left out. Deleted accounts disappear, and each page reads profile fields when it is fetched. Concurrent identity changes and clock skew at account creation can omit accounts. For a reconciled copy, pause identity changes and let them settle before the first page, then compare with the admin user list.

Each user has `id` (a stable ULID), `email`, `email_verified` (always `true`: possession was proven at account creation), `created_at`, `disabled` (disabled users are included) and `linked_providers` (`provider`, `client_id` and `subject`). Nothing else is exported: no password or hash, MFA or recovery material, session, token, key or application metadata. Importing the directory elsewhere carries over no credentials or sessions.

In CSV, `linked_providers` is a JSON array in one quoted cell. A cell that starts with `'`, `=`, `+`, `-`, `@`, a tab, CR or LF gets one extra leading apostrophe to stop spreadsheet formulas. On import, remove one leading apostrophe from any cell that starts with one. JSON keeps exact values.

## Current limits

- Verification, sign-in and password-reset emails use your project's name, logo, theme color and `email_message`. They are still sent from TerraScale's verified sender, with TerraScale's security copy and footer. Custom sender domains and your own mail credentials are not supported.

</div>
