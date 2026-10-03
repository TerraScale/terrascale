# Consumer identity competitive review

> Historical review from 2026-09-25, written against the Rust service. The
> findings and release gates still describe the target product; the C# port
> (TS-424) carries them forward, and `docs/parity.md` tracks what has landed.

Reviewed 2026-09-25. The target is consumer applications, especially Firebase
Authentication, Supabase Auth, and Amazon Cognito. Clerk, Auth0, WorkOS and
Keycloak are secondary benchmarks. Product documentation establishes what
competitors advertise; local source and executable tests establish what
ts-auth implements. Neither establishes deployed reliability or certification.

## Sources and product expectations

| Product | Official source reviewed | Competitive expectation |
| --- | --- | --- |
| Firebase Authentication | https://firebase.google.com/docs/auth | Consumer client SDKs, anonymous accounts, provider sign-in, phone authentication and account upgrades |
| Supabase Auth | https://supabase.com/docs/guides/auth | Anonymous users, identity linking, passwords, passwordless email, phone, passkeys, social providers, MFA, OAuth server and application authorization integration |
| Amazon Cognito | https://docs.aws.amazon.com/cognito/latest/developerguide/what-is-amazon-cognito.html | Managed consumer user pools, federation, managed login, MFA and integration with application/cloud access |
| Clerk | https://clerk.com/docs/guides/overview and https://clerk.com/docs/guides/organizations/control-access/roles-and-permissions | Easy integration, account/session UI, organization roles and custom permissions |
| Auth0 | https://auth0.com/docs/get-started/authentication-and-authorization-flow and https://auth0.com/docs/get-started/authentication-and-authorization-flow/device-authorization-flow | Broad standards interoperability, device authorization, extensibility and mature identity operations |
| WorkOS | https://workos.com/docs | AuthKit, enterprise SSO, directory sync, self-service administration, roles/permissions, events and abuse protection |
| Keycloak | https://www.keycloak.org/guides | Broad protocol support, identity brokering, configurable flows, themes, administration and operational tooling |
| Device flow specification | https://www.rfc-editor.org/rfc/rfc8628 | Client binding, explicit human approval, polling cadence, expiration and single redemption |

These are capability comparisons, not pricing, SLA or certification comparisons.
There is no defensible single “complete against every auth service” threshold:
the products include different databases, authorization systems, cloud services,
SDK ecosystems, fraud products and enterprise operations.

## Repository assessment

The starting point was `main` at `690fe7c`, including the Rust conversion. The
baseline workspace suite had 235 passing tests and one separately run browser
test. The service is Rust/Axum, its existing admin UI uses Datastar, and its
composed API catalog is available in OpenAPI and Scalar.

| Capability | Initial state | Current change or remaining acceptance |
| --- | --- | --- |
| Password signup, recovery, verified email | Implemented | Existing HTTP/security regression coverage; SMTP delivery still needs live acceptance |
| Magic links, email passcodes | Implemented | Existing mailbox/proof regressions; delivery, abuse and mobile continuation need further work |
| Passkeys and TOTP/recovery MFA | Implemented | Cryptographic/HTTP tests; complete browser/device interoperability is still a release gate |
| Anonymous/guest accounts | Missing | Added explicit client opt-in, quota-accounted guest identities, OAuth tokens and `is_anonymous` claims |
| Guest conversion | Missing | Added mailbox OTP verification and atomic conversion preserving user ID, quota seat and metadata; existing accounts cannot be merged or replaced |
| Browser API integration | OAuth clients stored unused origins | Added exact-origin CORS for OAuth bearer/form APIs; hosted cookie and management APIs stay outside CORS |
| Client configuration | Create/list/delete only | Added PATCH for name, redirect URIs, allowed origins and guest admission |
| OAuth device authorization | Missing | Added RFC 8628 endpoints, discovery, Datastar approval, durable polling limits and single redemption |
| Organization permissions | Free-form membership labels only | Added tenant-scoped permission definitions, versioned changes, permanent retirement and current-state authorization checks |
| OAuth session revocation | An authorization code could lose its session binding on redemption; access checks ignored `sid` | Redemption now rejects an inactive bound session; access-token verification checks a supplied `sid` |
| Sign out everywhere | Browser-session cleanup left OAuth refresh grants usable | Hosted and management revoke-all advance the credential epoch before cleanup, invalidating outstanding OAuth grants as well |
| Federation | Generic OIDC, GitHub and SAML implemented | Google/Microsoft/OIDC and SAML routes need real-provider interoperability evidence |
| SCIM, invitations, audit and webhooks | Implemented | Existing contract fixtures; real provider, recovery and delivery acceptance remain necessary |
| Billing and quotas | Implemented | Guest accounts reuse existing seat ownership; live Stripe acceptance remains unverified |
| Storage and production operations | Contract adapter and fixtures | Live TerraBase query compatibility, durability, regional failure handling and recovery are not established |

## Remaining consumer release gates, in priority order

1. **Live persistence acceptance.** Exercise every identity/token lifecycle
   against a compatible TerraBase member, including conditional writes, unique
   claims, timeouts, restarts and concurrent regional callers. The local HTTP
   fixture is not the database engine. Passing it cannot remove this gate.
2. **Consumer abuse controls.** Add end-to-end distributed password/login and
   recovery throttling, a supported bot-challenge integration, breached-password
   controls, email-delivery abuse monitoring and configurable policy. The new
   guest/device counters are narrow protections, not a complete fraud system.
3. **Phone authentication.** Add provider-backed SMS OTP, verified phone changes,
   phone MFA, resend/failure budgets and delivery receipts; validate with real
   provider credentials and numbers. No SMS implementation is claimed here.
4. **Consumer account lifecycle.** Finish self-service profile/email management,
   explicit provider linking/unlinking with proof of both identities, recent-auth
   deletion and data export. Guest conversion deliberately refuses merging with
   an existing account. That is not general account linking.
5. **Browser and native integrations.** Publish maintained client SDKs, sample
   applications, platform-specific deep-link handling and conformance tests.
   Validate Apple, Google and additional social providers with real callbacks.
   Device authorization does not replace native redirect/PKCE support.
6. **Policy and UX.** Configurable session lifetimes, MFA policy, account recovery,
   complete OAuth consent/re-authentication, custom domains, localized and
   accessible hosted flows, and consistent Datastar behavior across legacy
   hosted pages. The new device screen uses Datastar; this does not mean all
   existing hosted forms have been converted.
7. **Lifecycle operations.** Provision indexes and retention for challenges,
   device grants, audit data and inactive guests; support safe repair of retained
   uniqueness claims, credential migration/import and bulk user export. Guest
   cleanup must coordinate application-owned data rather than silently deleting it.
8. **Operational evidence.** Independent security review, protocol conformance,
   browser/mobile compatibility, load and resource measurements, live SMTP,
   observability/alerting, backup/restore exercises and rollout procedures.

Enterprise parity additionally needs richer authorization relationships, complete
SSO/directory interoperability, enterprise onboarding and stronger operational
evidence. The new permission checker is organization RBAC, not relationship-based
authorization or a replacement for application resource ownership checks.

## Verification map

- `consumer_accounts`: HTTP guest admission, verified conversion, stable
  subject/data, old-token rejection, proof isolation/attempt budgets, existing
  mailbox protection, CORS and disabling admission.
- `device_authorization`: discovery, poll cadence, explicit approval, CSRF/origin
  binding, wrong-client rejection, concurrent redemption, expiry and revocation;
  separately enabled Chromium test exercises the real Datastar form and patch.
- `authorization_acceptance`: management scopes and tenant isolation, role
  version preconditions, permanent retirement, identity/membership eligibility
  and all-of permission decisions.
- Existing workspace tests cover the pre-existing auth, federation, directory,
  quota, audit, webhook and operational surfaces.

Validation on the pinned Rust 1.98.1 toolchain and `ts-rust` revision
`c590cd41eabf05bbd5982a1580c1bb01880c346a`: 245 workspace tests passed, with the
two explicit Chromium tests also passing. Formatting, Clippy with warnings
denied, the RSA production-usage guard and the locked dependency audit passed.
The audit retains only the repository's documented `RUSTSEC-2023-0071`
exception. The local live-stack endpoint (`127.0.0.1:8420`) was unreachable,
so no live TerraBase acceptance is claimed.

The optimized release binary built and passed a local process smoke check:
`/healthz` returned 200, `/scalar` returned 200, `/readyz` returned 503 with
storage deliberately unavailable, and the served OpenAPI document contained
121 paths, the new operations, and resolvable local schema references.

These changes close specific gaps. They do not establish feature parity with
Firebase, Supabase, Cognito or every identity provider, and do not authorize a
production readiness claim.
