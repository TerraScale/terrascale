---
title: Authentication capabilities
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Authentication capabilities

Current contract, reviewed 26 October 2026. This page takes precedence over older capability lists; it is not an all-protocol certification. [Application sign-in](/ru/guides/end-user-authentication), [API credentials](/ru/guides/authentication) and [organization SSO](/ru/guides/single-sign-on) have separate scopes.

| Capability | Availability and scope |
| --- | --- |
| Email code, password, sign-up and reset | Hosted sign-in for TerraScale accounts and project end users, subject to account policy. |
| TOTP and recovery codes | Authenticator second factor; recovery codes replace the second factor, not primary sign-in. |
| Social sign-in | Project Google, GitHub, Apple and generic OIDC connections require provider registration and verified email. |
| Organization SSO | OIDC for a verified email domain. SAML is not currently available. |
| Authorization code and refresh | Platform and project issuers; S256 PKCE and registered redirects required. Project issuers serve only these grants. |
| Device authorization | TerraScale CLI only: `POST /terrascale/oauth/device`, approval at `/ui/terrascale/device`, polling at `/terrascale/oauth/token`. Not `/oauth/device_authorization`. |
| Machine credentials and scoped exchange | Platform issuer and authorized clients only; these are not project-issuer grants. |
| Discovery, JWKS, userinfo, revoke and logout | Available per issuer; use its discovered endpoints. Introspection is not available. |
| Passkeys, magic-link sign-in, guest accounts and conversion | Not currently available. Email template names and staged examples do not establish a supported sign-in method. |

For issuer `https://auth.terrascale.tech/<tenant>`, read `<issuer>/.well-known/openid-configuration`. `<tenant>` is `terrascale` for platform accounts or your project ID for end users. [Served OpenAPI](https://auth.terrascale.tech/openapi/v1.json) and [API reference](https://auth.terrascale.tech/scalar/) describe the current deployment. Discovery may list grants or methods unavailable to your registered client; it does not grant permissions. Project client registration does not provision `private_key_jwt` assertion keys. Although listed in metadata, `prompt=login`, `consent` and `select_account` currently return interaction errors; `prompt=none` can check an existing session.

Token and revoke requests use form encoding. Public clients send `client_id` without a secret. Confidential clients use HTTP Basic (`client_secret_basic`) or form `client_id` and `client_secret` (`client_secret_post`), keeping secrets server-side. An empty OpenAPI security alternative covers requests without an Authorization header, including form authentication; client validation still applies. Always validate state, nonce, issuer, audience, signature and expiry with a maintained OIDC client. ID tokens are not API access tokens, and project OIDC tokens do not authorize TerraScale data APIs.

Refresh tokens rotate: securely replace the predecessor with its successor. Never retry a consumed code automatically; on `invalid_grant`, restart sign-in. Revocation and clearing your application session are separate. Offline JWT validation does not learn revocation immediately; enforce expiry and your API's revocation policy. OIDC access tokens last at most five minutes; API-key and scoped data tokens have separate lifetimes.

## Refresh scopes and lifetime

Refresh requests follow [RFC 6749 section 6](https://www.rfc-editor.org/rfc/rfc6749#section-6):

| Requested `scope` | Result |
| --- | --- |
| Omitted or equal to the original authorization | Original authorized scopes. |
| A subset of the original authorization | Only that subset in the access token and its UserInfo permissions. |
| Expanded or unknown scopes | OAuth error, with no added permissions; the refused request does not consume the refresh token. |

A replacement refresh token retains the **original authorized scopes**, even after a reduced-scope access token. For example, refreshing `openid email offline_access` with `scope=openid` omits email from UserInfo; the successor can request the original scopes again. This never grants scopes outside the original authorization. Sign-in scopes are limited to `openid`, `email`, `profile` and `offline_access`, subject to client permissions; resource permissions require a separate role-checked token exchange.

Platform refresh families expire at most **14 days after the initial token exchange**. Project refresh families follow the project's session policy, up to **30 days from the original authentication**. Rotation does not extend those deadlines. Revocation or a finite rotation limit can require sign-in earlier; these lifetimes do not promise unlimited refreshes. Stop automatic retries on `invalid_grant` and start sign-in again.

Cross-origin browser `fetch` to ts-auth is not currently supported through an API CORS policy. Use hosted browser navigation and an appropriate backend exchange; never put confidential client secrets in a browser. Redirect registration does not grant CORS permission. Existing data-API CORS behavior is a separate contract. Staged `allowed_origins`, `allow_anonymous`, guest-upgrade and SAML examples are not available setup instructions. To report a conflicting claim, use [support](/ru/legal/support) with the page URL and observed route/status, without credentials or customer data.

</div>
