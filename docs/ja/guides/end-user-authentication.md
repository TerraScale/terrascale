---
title: End-user authentication
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

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
- End-user tokens do not authorize requests to the TerraScale database API. Have your server call TerraScale with an API key ([Authentication](/ja/guides/authentication)) after checking the end user's token.

</div>
