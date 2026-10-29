# Session-bound CSRF proofs

Browser forms use a signed double-submit proof. The `__Host-ts-dashboard-csrf`
cookie holds a random nonce; the `__Host-` prefix makes browsers refuse it from a
sibling subdomain or with a `Domain` attribute, and it is always `Secure`,
`HttpOnly`, `SameSite=Strict`, `Path=/`. The form field is
`HMAC-SHA256(k, binding "." nonce)`, where `k` is derived with HKDF from the
existing session encryption key (no new key or storage) and `binding` is the
browser's dashboard session handle, else its account-deletion recovery cookie
(the original session handle, kept after ts-auth revokes it), else empty for
anonymous forms such as MCP OAuth consent.

A proof is valid only for the session it was rendered for: signing in, signing
out or a different session invalidates it. Exact `Origin` and `Sec-Fetch-Site`
checks still run first. Integration coverage: `CsrfBindingJourneyTests`.
