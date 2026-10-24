---
title: Single sign-on
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Single sign-on

Organization owners can connect the organization's identity provider (IdP) over OpenID Connect (OIDC). After you verify your email domain, people who sign in with an address at that domain use your IdP instead of a TerraScale password or email code.

Single sign-on is available on **Team** and **Enterprise** plans. See [pricing](/es/pricing/) for plan details.

Single sign-on in the dashboard and the API is OIDC only for now. SAML 2.0 support is planned. Any standards-compliant OIDC provider works, for example Okta, Microsoft Entra ID, Google Workspace, Keycloak or Authentik.

| Item | Value |
| --- | --- |
| Hosted sign-in | `https://auth.terrascale.tech` |
| Redirect URI (register at your IdP) | `https://auth.terrascale.tech/ui/terrascale/sso/callback` |
| Scopes | `openid email profile` |
| Flow | Authorization code with PKCE (`S256`) and `client_secret_basic` |
| ID token checks | Signature (issuer JWKS), issuer, audience, expiry and nonce |

## Set up single sign-on

1. **Register TerraScale at your IdP.** Create a confidential OIDC client (one with a client secret). Add the redirect URI from the table above, and allow the scopes `openid`, `email` and `profile`. Keep the client ID and client secret for step 3.
2. **Open the organization's settings.** Select the organization in the dashboard and open its settings. In the **Single sign-on** section, enter your email domain (for example `acme.com`), the default role, the issuer, the client ID and the client secret.
3. **Save the connection.** The API equivalent is a `PUT` to `/v1/terrascale/organizations/{org}/sso`:

```bash
curl -sS --fail-with-body -X PUT \
  -H "Authorization: Bearer $TERRASCALE_ACCESS_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "acme.com",
    "default_role": "member",
    "oidc": {
      "issuer": "https://idp.acme.com/realms/acme",
      "client_id": "terrascale",
      "client_secret": "<client secret from your IdP>"
    }
  }' \
  "https://auth.terrascale.tech/v1/terrascale/organizations/$ORG_ID/sso"
```

4. **Verify the domain.** The response contains a `verification` field with a DNS TXT record to publish:

   | Field | Value |
   | --- | --- |
   | Name | `_terrascale-sso.<domain>`, for example `_terrascale-sso.acme.com` |
   | Value | `terrascale-sso-verification=<token>` |

   Publish the record at your DNS provider. Then select **Verify** in the dashboard, or send `POST /v1/terrascale/organizations/{org}/sso/verify`. Until DNS shows the record, verification answers `409` with `domain_unverified`. Wait for the record to propagate and try again.

Only the organization's owners can create, change, verify or delete the connection. Admins can read it with `GET /v1/terrascale/organizations/{org}/sso`.

## Connection settings

| Field | Required | Meaning |
| --- | --- | --- |
| `domain` | Yes | The email domain that uses single sign-on. |
| `default_role` | No | Role for people who join through single sign-on: `viewer`, `member` (default) or `admin`. |
| `oidc.issuer` | Yes | Your IdP's issuer. It must use `https`, and its `/.well-known/openid-configuration` must name the same issuer. |
| `oidc.client_id` | Yes | The client ID from your IdP. |
| `oidc.client_secret` | Yes on create | The client secret from your IdP. It is write-only. On updates, omit it to keep the stored secret. |

## What happens at sign-in

- **Domain sign-in.** After verification, anyone who enters an email address at your domain on the hosted sign-in page is sent to your IdP through **Continue with single sign-on**. Email-code and password sign-in are refused for that domain.
- **Break-glass owners.** Owners of the organization keep email-code and password sign-in, so a broken IdP cannot lock the organization out.
- **Verified email only.** The IdP's email must belong to the verified domain. A token with `email_verified` set to `false` is refused.
- **Just-in-time membership.** The first single sign-on sign-in creates the TerraScale account if it does not exist, then adds it to the organization with the connection's `default_role`. Existing members keep their current role.
- **One organization per domain.** A verified domain belongs to one organization. No other organization can claim it.

## Remove the connection

`DELETE /v1/terrascale/organizations/{org}/sso` (owners only) removes the connection. Normal sign-in returns for the domain. Memberships stay in place, so people keep their organization access and role.

## Example: Keycloak

1. In your realm, create a client with the OpenID Connect protocol and the client ID `terrascale`.
2. Turn on client authentication, so the client is confidential, and copy the client secret from the client's credentials.
3. Add `https://auth.terrascale.tech/ui/terrascale/sso/callback` as a valid redirect URI.
4. Set the connection's issuer to `https://<host>/realms/<realm>`. The discovery document at `https://<host>/realms/<realm>/.well-known/openid-configuration` must report that same issuer.

Use the values from this client in the `oidc` object shown above.

## Troubleshooting

- **`409 domain_unverified`.** The TXT record is missing or has not propagated. Check the record name and value, then verify again.
- **Sign-in refused for an address at your domain.** Check that the IdP's email is in the verified domain and that its `email_verified` claim is `true`.

</div>
