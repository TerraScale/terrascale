---
title: End-user authentication
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

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

## Set up sign-in in the dashboard

Open **Authentication** for your project.

1. **OAuth applications**: register your application with a name, its type and its redirect URIs, one per line. Use **public** for browser, mobile and desktop apps (no secret) and **confidential** for server-side apps. A confidential application's client secret is shown once, right after you create it; store it in your server's secret store. Redirect URIs must be `https`, or `http` on `localhost` and loopback addresses.
2. **Sign-in methods**: choose email codes, passwords, or both, and whether people can create their own accounts. At least one of email codes and passwords stays on. A method you turn off disappears from the hosted pages.
3. **Branding**: set the name, an `https` logo URL and a `#rrggbb` theme color for the hosted pages. Without a name, the pages show your project's name.

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

Use any OpenID Connect client library and point it at the discovery URL. The flow is the standard one:

1. Send the browser to the authorization endpoint with `response_type=code`, your `client_id`, a registered `redirect_uri`, the scopes, `state`, and a PKCE `code_challenge` with `code_challenge_method=S256`.
2. A user without a session signs in or signs up on the hosted pages and comes back to your `redirect_uri` with `code` and `state`.
3. Redeem the code at the token endpoint with the `code_verifier` (public clients send `client_id`; confidential clients authenticate with their secret).

```bash
curl -sS --fail-with-body https://auth.terrascale.tech/$PROJECT_ID/oauth/token \
  -d grant_type=authorization_code -d client_id=$CLIENT_ID \
  -d code=$CODE -d redirect_uri=$REDIRECT_URI -d code_verifier=$CODE_VERIFIER
```

The access token is a JWT whose `iss` is your project's issuer, whose `aud` is your client ID and whose `sub` is the user's ID. Validate tokens in your own API against the project's JWKS from discovery, checking issuer, audience and expiry. `/oauth/userinfo` answers `sub`, and `email` with the `email` scope. Access tokens last 5 minutes; refresh tokens rotate on every use and expire 14 days after sign-in. Sign users out through `/oauth/logout`, and revoke tokens through `/oauth/revoke`.

A project issuer serves only the authorization code and refresh token grants. Its tokens are for your own application: TerraScale's APIs do not accept them.

## Manage users

The dashboard lists your project's users with search. For each user you can see their sessions, end a session, **disable** them or **delete** them. Disabling a user ends every session and stops their refresh tokens at once; access tokens already issued stay valid until they expire, at most 5 minutes. Deleting a user removes the account; the address can sign up again.

The same operations are available in the TerraScale SDKs for automation.

## Current limits

- Authentication emails, such as sign-in codes, carry TerraScale's branding, not your project's.
- End-user tokens do not authorize requests to the TerraScale database API. Have your server call TerraScale with an API key ([Authentication](/ru/guides/authentication)) after checking the end user's token.

</div>
