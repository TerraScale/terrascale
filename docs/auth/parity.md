# ts-auth feature parity matrix

Every user-facing feature of Clerk, WorkOS, and Auth0, and where it lives in
ts-auth. Statuses reflect the code on `main` — each ✅ is backed by tests.

## Authentication

| Feature | Clerk | WorkOS | Auth0 | ts-auth | Where |
| --- | --- | --- | --- | --- | --- |
| Email + password | ✅ | ✅ | ✅ | ✅ | `/ui/{slug}/sign-up`, `/sign-in` (Argon2id) |
| Passkeys / WebAuthn | ✅ | ✅ | ✅ | ✅ | `/ui/{slug}/passkeys/*` (discoverable credentials, clone detection) |
| Magic links | ✅ | ✅ | ✅ | ✅ | `/ui/{slug}/magic-link` |
| Email passcodes (first factor) | ✅ | – | ✅ | ✅ | `/ui/{slug}/passcode/*` |
| SMS passcodes | ✅ | – | ✅ | ➖ | `ISmsSender` interface; logging default (no SMS provider wired) |
| Social login (OIDC) | ✅ | ✅ | ✅ | ✅ | Connections: `/ui/{slug}/connections/{id}/oidc/*` |
| Enterprise SSO (SAML) | ✅ | ✅ | ✅ | ✅ | Connections: `/ui/{slug}/connections/{id}/saml/*` (XML-DSig verified) |
| MFA — TOTP | ✅ | ✅ | ✅ | ✅ | `/ui/{slug}/mfa/*` (RFC 6238 + recovery codes) |
| MFA — passkey as factor | ✅ | – | ✅ | ✅ | passkeys satisfy the step-up during sign-in |
| Password reset | ✅ | ✅ | ✅ | ✅ | `/ui/{slug}/forgot-password`, `/reset-password` |
| Email verification | ✅ | ✅ | ✅ | ✅ | `/ui/{slug}/verify-email` |
| Blocking/banning users | ✅ | ✅ | ✅ | ✅ | Management API `PATCH /v1/{slug}/users/{id}` |
| Impersonation | ✅ | – | ✅ | ✅ | `POST /v1/{slug}/users/{id}/impersonate` |

## OAuth 2.0 / OIDC provider (your app's users logging into downstream apps)

| Feature | Clerk | WorkOS | Auth0 | ts-auth | Where |
| --- | --- | --- | --- | --- | --- |
| Authorization code + PKCE | ✅ | ✅ | ✅ | ✅ | `/{slug}/oauth/authorize`, `/oauth/token` |
| Refresh token rotation + reuse detection | ✅ | ✅ | ✅ | ✅ | token endpoint (family revocation) |
| Client credentials (M2M) | ✅ | – | ✅ | ✅ | token endpoint |
| Discovery + JWKS | ✅ | ✅ | ✅ | ✅ | `/{slug}/.well-known/*` |
| Userinfo / introspection / revocation | – | – | ✅ | ✅ | `/{slug}/oauth/*` |
| RP-initiated logout | ✅ | ✅ | ✅ | ✅ | `/{slug}/oauth/logout` |
| Custom JWT templates | ✅ | – | ✅ (rules) | ✅ | `/v1/{slug}/jwt-templates` + mint |

## User & session management

| Feature | ts-auth | Where |
| --- | --- | --- |
| User CRUD, metadata (public/private) | ✅ | Management API `/v1/{slug}/users*` |
| Server-side sessions, device list, revoke | ✅ | `/ui/{slug}/account/sessions`, management API |
| API keys with scopes | ✅ | `/v1/{slug}/api-keys` |
| Rate limiting | ✅ | fixed window per key per 5 min |
| Webhooks (HMAC-signed, retried) | ✅ | `/v1/{slug}/webhooks` + durable delivery records, dead-letter listing/replay, and dispatcher |
| Audit logs + query API | ✅ | `/v1/{slug}/audit-events`, SHA-256 integrity and optional HMAC authenticity seals |

## Organizations (B2B)

| Feature | Clerk | WorkOS | ts-auth | Where |
| --- | --- | --- | --- | --- |
| Organizations + roles | ✅ | ✅ | ✅ | `/v1/{slug}/organizations*` |
| Memberships + custom roles | ✅ | ✅ | ✅ | `/v1/{slug}/organizations/{id}/memberships` |
| Invitations | ✅ | ✅ | ✅ | `/v1/{slug}/organizations/{id}/invitations` + accept page |
| Domain enrollment | ✅ | ➖ | ✅ | `/v1/{slug}/organizations/{id}/domains` + enroll |
| Directory sync (SCIM 2.0) | ✅ | ✅ | ✅ | `/v1/{slug}/directories` + `/scim/{dirId}/*` |
| Admin portal (self-serve connection setup) | ✅ | ✅ | ➖ | hosted pages: manage SSO connections, view SCIM directories |

## Platform

| Feature | ts-auth | Where |
| --- | --- | --- |
| Multi-tenant instances | ✅ | every entity keyed by instance; atomic identifier claims |
| Hosted UI with branding | ✅ | `Ui.Page` theme/logo from `PATCH /v1/{slug}/instance` |
| Pluggable email/SMS | ✅ | `IEmailSender` / `ISmsSender` |
| Stripe billing + plan quotas | ✅ | `/v1/{slug}/billing*` + webhook, including authenticated quota-seat reconciliation (see below) |
| Payments (checkout, portal) | ✅ | Stripe Checkout + Billing Portal sessions |
| Subscription lifecycle via webhooks | ✅ | `/v1/billing/webhook` (signature-verified) |

## Deliberately not built (roadmap)

- **Clerk Actions** (in-request JS hooks): a JS sandbox is out of scope for a
  from-scratch service; signed webhooks cover the event surface.
- **Front/back-channel logout** for downstream OIDC clients: RP-initiated
  logout ships; channel logout is on the roadmap.
- **DNS-verified org domains**: enrollment domains are admin-declared today;
  DNS TXT verification is on the roadmap.
- **SIEM streaming exports** for audit logs: query API ships; streaming is on
  the roadmap.
