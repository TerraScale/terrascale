# Adversarial review charters

Each reviewer executes exactly one charter. Adversarial means: assume the
feature is broken until code + tests prove otherwise. Verify by reading code
and running the relevant tests; fix real defects minimally in the correct
place; add a regression test when practical; leave the full suite green.

1. Crypto vectors: re-verify Argon2id, BLAKE2b, TOTP against their RFC test vectors; check PHC parse edge cases; hunt timing leaks in secret comparisons (non-constant-time paths).
2. Password hashing: hash/verify paths, null-password users (password-less), empty/oversized passwords, no plaintext secrets in logs or documents.
3. JWT core: algorithm confusion, expired/tampered tokens, kid spoofing (unknown kid), exp/iat semantics, `decode_unverified` misuse anywhere in production code.
4. KeyRing + SecretBox: AES-GCM nonce uniqueness, key persistence (double generation race), master key length enforcement, decrypt failure paths.
5. Sessions: cookie flags (HttpOnly/Secure/SameSite), expiry math, revocation, `last_active_at` write amplification, resolve rejects banned/deleted users, cross-instance token reuse.
6. Sign-in flow: user enumeration via timing/responses, open-redirect via every `next` variant (protocol-relative, backslash, encoded), banned/deleted handling, MFA gate ordering.
7. Sign-up flow: duplicate email via the atomic claim layer (not just the friendly pre-check), quota gate ordering vs claim rollback, validation gaps.
8. Magic links + email tokens: single-use enforcement, expiry, cross-instance and cross-purpose token reuse, consume-once under double POST.
9. Password reset: token reuse after reset, all sessions revoked, enumeration via forgot-password, reset of banned/deleted users.
10. Email verification: EmailVerified correctness on change-email (must reset), verify of another user's token, re-verification flows.
11. OAuth authorize: PKCE required for public clients (and "plain" rejected), redirect_uri EXACT match, state/nonce echo, prompt=none error redirect, code TTL/consume.
12. OAuth token endpoint: code replay, redirect_uri mismatch, client auth (basic vs post, wrong secret), RFC 6749 error codes and statuses.
13. Refresh tokens: rotation on use, reuse detection revokes the family, expired refresh, cross-client token use.
14. Introspection + revocation: RFC 7662/7009 conformance, auth required, revoked/rotated refresh states, access-token introspection truthfulness.
15. id_token: iss/aud/nonce/auth_time/at_hash correctness (compute at_hash per spec: half the hash bytes of the access token), email claims only with scope.
16. Discovery + JWKS: exact OIDC field names/shapes, JWKS keys match KeyRing actives, userinfo scope gating, WWW-Auth handling.
17. RP-initiated logout: post_logout_redirect_uri validation (must be a registered redirect origin, not prefix-matched loosely), session kill, cookie clear.
18. WebAuthn registration: challenge single-use consumed BEFORE user creation side effects, rpId/origin strict checks, duplicate credential 409, UP/UV flag handling, attestation "none" empty statement rule.
19. WebAuthn assertion: sign-count clone detection on first assert (count 0), credential-id mismatch rejection, signature over authData||clientDataHash, session + LastSignInAt + audit side effects.
20. WebAuthn stored keys: CBOR round-trip fidelity (`from_stored`), RSA (RS256/PS256) verification path, transports/nickname fields, list never leaks key material.
21. TOTP MFA: enroll→confirm unconfirmed-state rule, duplicate enrollment rejection, recovery codes single-use + regenerate invalidates old set.
22. MFA verify: pending token lifecycle (consume on success only), wrong-code retries allowed but bounded by TTL, redirect validation, session cookie set exactly once.
23. Email passcodes: resend throttle, lockout after failures, enumeration resistance (ok:true always), mfa_required path creates exactly one session.
24. Organizations: CRUD, slug derivation safety, membership claim atomicity (duplicate member 409), role patch delete+recreate window.
25. Invitations: accept requires matching signed-in email, expired/revoked/pending states, token single-use, membership created once.
26. Org domains: enrollment matching (case-insensitivity, subdomain NOT matching), duplicate domain 409, enroll idempotency, sign-up auto-join correctness.
27. Management users: pagination `starting_after` ordering and correctness, PATCH nullable-field semantics (explicit null vs absent), soft-delete completeness, impersonation creates a real session + audit.
28. Management API keys: create returns sk_ once, list never returns secrets, revoke immediately 401s, cross-instance key rejected (401) everywhere it is used.
29. Rate limiter: window math at boundaries, concurrent increments, 429 envelope + Retry-After, limiter applied to ALL management endpoints uniformly.
30. Webhooks: signature scheme exactness (timestamp.header form, constant-time), wildcard/prefix matching, retry backoff, durable delivery-checkpoint restart semantics, secret exposure in list/audit.
31. Audit events: no secrets/tokens/hashes leaked into audit `data`; event types are consistent across emitters; actor correctness.
32. SCIM conformance: RFC 7644 list response fields (Resources/totalResults/startIndex/itemsPerPage), error envelope schemas, PATCH replace semantics, bearer 401 shape.
33. SCIM provisioning: deprovision bans + drops memberships + releases email claim; userName change re-keys claims; groups membership consistency with users.
34. OIDC connections: state lifecycle; **unverified-email account linking** — the callback must NOT link/create a user when the upstream email is unverified (account takeover vector); JWKS cache staleness on key rotation; client secret boxed; errors never 500.
35. SAML connections: XML signature wrapping (verify the signed element is the one validated — reference URI match), audience strictness, conditions skew bounds, RelayState single-use, unsigned response rejection.
36. Admin portal: org-admin authorization enforced on every route, connection create validation (PEM parses, URLs absolute), HTML injection via connection/org names, enable/disable authorization.
37. Billing webhooks: signature verify (skew, multiple v1, tamper), replay dedupe bounds, checkout.session.completed customer+plan linking, subscription transitions, non-descriptive 401.
38. Billing checkout/portal: form encoding exactly per Stripe (nested params), 503/400 paths, customer requirement for portal, plan resolution from price ids.
39. Quota enforcement: EVERY user-creation path gates on the plan quota — hosted sign-up, management create, SCIM provisioning, OIDC/SAML auto-create, passcode verify; bypass attempts.
40. Uniqueness claims: claim released on EVERY delete path (passkey, membership, org, client, scim token, domain), rollback on failed create leaves no orphans, release idempotent.
41. Instance settings + branding: logoUrl must be http(s) absolute (no javascript:), themeColor format, branded pages escape output.
42. Hosted UI XSS: every interpolated value in every hosted page and Datastar fragment is escaped (names, emails, connection names, org names, nicknames, errors); CSRF posture of state-changing form POSTs (SameSite cookie reliance documented).
43. Instance resolution (`resolve_instance`): token parse (sk_ format), revoked key rejection, slug binding (key of instance A cannot touch instance B's routes) across ALL modules using it.
44. AuthStore: typed query deserialization could silently drop corrupt docs — verify query paths can't return wrong-entity documents; only not-found is treated as absence; list pagination correctness.
45. Test hygiene: tests that assert nothing meaningful; shared process-global state between parallel tests; env pollution (TS_AUTH_* set by one test leaking into another); parallel-safe fixture usage.
46. Parity audit: every row in docs/parity.md verified against actual code + a passing test; fix the doc or file the gap as an issue and fix it.
47. README accuracy: every documented route exists in code; every env var in AuthOptions is documented and vice versa; commands work as written.
48. Options: validation coverage (empty strings, whitespace, bad base64, bad URLs), defaults, arg-overrides-env precedence.
49. Concurrency: process-local state (management rate limiter, readiness cache, key-ring creation gates, password-hashing semaphore) and durable claims (webhook delivery checkpoints, passcode attempt counters, Stripe event claims) — thread safety, unbounded growth, restart semantics; fix real races.
50. Final sweep: run the full suite three times hunting flakes; `cargo fmt --all -- --check` and strict `cargo clippy --workspace --all-targets --locked -- -D warnings`; cross-check parity.md and README against any behavior changed by reviewers 1–49; final commit.
