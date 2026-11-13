# A ts-auth restart signs nobody out

Reported 2026-10-29: "ts-auth keeps ASP.NET DataProtection keys in memory, so every restart invalidates every cookie
and antiforgery token". ts-auth does log ASP.NET's warning about storing keys in a non-persisted directory. But no
ts-auth credential, cookie or form depends on DataProtection:

| What | How it is protected | Survives a restart because |
| --- | --- | --- |
| Hosted session (`tsauth_session`) | opaque random token, its hash stored in TerraBase (`sessions`) | the record is in TerraBase |
| Authorization codes, refresh tokens | OpenIddict reference tokens, signed and encrypted with keys derived from the OCI Vault master key | every replica derives the same keys |
| Access and identity tokens | ES256, signed by the key ring stored encrypted in TerraBase | the ring is in TerraBase |
| Social and SSO state cookies | sealed with a key derived from the master key | every replica derives the same key |
| Hosted form posts | Origin check against the public origin; no antiforgery tokens | no per-process secret |

`OpenIddict.Server.DataProtection` is not referenced, and ts-auth never calls `IDataProtector` or antiforgery.

Adding a TerraBase-backed DataProtection key ring would protect nothing today, so none is added. If a future feature
uses `IDataProtector`, antiforgery or cookie authentication, it must first persist the ring: an `IXmlRepository` on
an auth-owned TerraBase table, with keys encrypted by an `IXmlEncryptor` keyed from an HKDF subkey of the master
key. If users are signed out by ts-auth restarts, look at the client's own session store, for example ts-dashboard's
cookies.

## TerraBase restarts while ts-auth is unavailable

Each TerraBase node keeps its last successful public verification-key set in its own data directory as
`auth-signing-keys.json`. A restart can load this issuer-bound snapshot after the peer/kernel clock-trust gate opens, while fetching independently.
Its remaining 24-hour allowance runs on monotonic time, with a persisted wall-time high-water mark across restarts.
Rollback before the fetch time or high-water mark rejects the file. Cross-restart elapsed-time accounting assumes
accurate trusted UTC and durable local state; see the guarantees and limitations in
[the authentication decision](../../terrabase/decisions/0007-ts-auth-user-tokens.md). Removed keys keep verifying until the node’s next successful refresh or that
maximum age, with token expiration and API-key admission still enforced. With no usable snapshot, requests answer
`503 auth_unavailable` with `Retry-After: 1` while background fetches continue. Watch
`terrabase_auth_signing_keys_loaded` (zero for one minute) and `terrabase_auth_signing_keys_age_seconds` (above 1200
for five minutes). See [TerraBase signing-key recovery](../../terrabase/signing-key-operations.md) for retry bounds
and the revocation window.
