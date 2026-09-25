# Rust capability and verification matrix

The active service is the Cargo workspace. Each row names the Rust owner and
the evidence currently available; it does not imply acceptance against a live
TerraBase deployment. The previous .NET source and tests remain available in
Git history for historical comparison only.

| Capability | Rust surface | Current verification |
| --- | --- | --- |
| Consumer guest accounts and verified conversion | `features/oauth/anonymous`, `auth-store::upgrade_anonymous_user` | HTTP tests for stable identity/data, old credential rejection, proof isolation, attempt budgets and existing-mailbox protection |
| Browser OAuth CORS and mutable client settings | `features/oauth/cors`, `features/oauth` | HTTP tests for exact origins, preflight, credential exclusion and disabling guest admission |
| Device authorization | `features/oauth/device` | HTTP polling/approval/concurrency/expiry/revocation tests; explicit Chromium Datastar acceptance test |
| Organization role permissions | `features/authorization` | HTTP tenant/scope/versioning/retirement and current-state authorization tests |
| Hosted users, sessions, email links, password recovery | `features/users`, `features/email` | Real HTTP listener with a TerraBase contract fixture in `http_surface` and `hosted_identity_security` |
| Passkeys, passcodes, MFA | `features/passkeys`, `features/passcodes`, `features/mfa`; `auth-security` WebAuthn/TOTP | Rust unit and security regressions; the full browser ceremony still needs live browser acceptance |
| OAuth 2.0/OIDC provider, JWKS, JWT templates | `features/oauth`, `features/templates`, `auth-security` JWT | HTTP PKCE/code-consumption test and Rust token/template tests |
| OIDC, GitHub, SAML federation | `features/connections`; `features/admin` setup | Signed SAML fixture tests and route tests; live upstream provider interoperability remains unverified |
| Organizations, invitations, enrollment domains | `features/organizations` | HTTP contract-fixture regressions for invitation retry, membership scope, and verified-domain enrollment |
| SCIM directories, users, groups | `features/scim` | Passing HTTP contract-fixture token, rotation, directory-scope and concurrent isolation regressions |
| Management keys, scopes, user API, operator bootstrap | `features/management`, `features/instances` | HTTP scope/rate-limit tests and OpenAPI tests, including ambiguous-write reconciliation behavior |
| Hosted admin portal and Datastar fragments | `features/admin`, embedded assets | HTTP SSE/escaping tests and focused admin tests; browser interaction remains a separate acceptance check |
| Billing, quota seats, Stripe webhooks | `features/billing`, `auth-store` | HTTP checkout/webhook contract fixture and quota/store tests; live Stripe integration remains unverified |
| Webhooks and audit integrity | `features/webhooks`, `auth-store` audit export | Rust store/unit tests for durable records, HMAC seals, and offline export verification |
| Health, readiness, private metrics, OpenAPI, Scalar | `features/observability`, `app` | Real listener HTTP tests for authentication, fail-closed readiness, `/openapi/v1.json`, and `/scalar` |
| Global GRU storage authority | `app`, `auth-store`, `ts-rust` | Configuration and contract-fixture checks; live multi-region TerraBase behavior remains unverified |

The loopback TerraBase HTTP fixture exercises the real Axum router, `ts-rust`
SDK calls, and `auth-store` adapter. It is not the TerraBase engine. The live
database implementation is unfinished, so this suite cannot establish
deployed database compatibility, regional failover behavior, or durability.
Run the optional `scripts/smoke-real-stack.sh` only with a separately running
compatible member when that contract becomes available. Persistent file
uploads are not implemented; admin assets and fonts are embedded in the Rust
binary. If persistent files are added, they use RustFS through S3.

The Rust source and tests are the acceptance source of truth. Historical .NET
test results do not establish Rust feature parity.
